using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;
using PdmLite.Library.Rules;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Import;

/// <summary>
/// Добавление компонентов в библиотеку: анализ файлов → проверки → внесение
/// (копирование в папку библиотеки, запись свойств, запись в базу).
/// </summary>
public sealed partial class LibraryImportService(
    IPdmDatabase database,
    IDocumentPropertyStore properties,
    Func<LibraryStorage> storageFactory,
    ILogger<LibraryImportService> logger)
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "OldVersions" };
    private static readonly string[] InventorExtensions = [".ipt", ".iam"];

    /// <summary>Сканирует файлы и папки, читает свойства, предлагает обозначение и раздел, проверяет.</summary>
    public async Task<ImportSession> AnalyzeAsync(IEnumerable<string> inputs, IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct).ConfigureAwait(false);
        await AddAsync(session, inputs, progress, ct).ConfigureAwait(false);
        return session;
    }

    /// <summary>Добавляет файлы к сеансу (уже добавленные пропускаются) и перепроверяет список.</summary>
    public async Task AddAsync(ImportSession session, IEnumerable<string> inputs, IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var known = session.Candidates.Select(c => c.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Enumerate(inputs, storageFactory(), session.Skipped).Where(known.Add).ToList();

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ImportProgress(i, files.Count, Path.GetFileName(files[i])));
            session.Candidates.Add(await AnalyzeFileAsync(files[i], session, ct).ConfigureAwait(false));
        }

        progress?.Report(new ImportProgress(files.Count, files.Count, null));
        Validate(session);
    }

    private async Task<ImportSession> CreateSessionAsync(CancellationToken ct)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        await LibraryCatalog.SeedAsync(db, ct).ConfigureAwait(false);

        var catalog = await LibraryCatalog.LoadAsync(db, ct).ConfigureAwait(false);
        var items = await db.LibraryItems.AsNoTracking()
            .Select(i => new { i.DesignationKey, i.Designation, i.FileHash, i.SourceHash, i.RelativePath, i.DocumentType, i.Volume, i.Area })
            .ToListAsync(ct).ConfigureAwait(false);

        var keys = items.ToDictionary(i => i.DesignationKey, i => i.Designation);
        var hashes = new Dictionary<string, string>();
        var paths = new Dictionary<string, string>();
        foreach (var item in items)
        {
            hashes.TryAdd(item.FileHash, item.Designation);
            paths.TryAdd(item.FileHash, item.RelativePath);
            if (item.SourceHash is not null)
            {
                hashes.TryAdd(item.SourceHash, item.Designation);
                paths.TryAdd(item.SourceHash, item.RelativePath);
            }
        }

        var rules = await db.DesignationRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var session = new ImportSession(catalog, keys, hashes, paths, properties.IsAvailable, properties.UnavailableReason)
        {
            Rules = new DesignationRuleSet(rules),
        };
        foreach (var item in items.Where(i => LibraryGeometry.Known(i.Volume, i.Area)))
            session.ExistingGeometry.Add((item.Designation, item.DocumentType, item.Volume!.Value, item.Area!.Value));
        return session;
    }

    private static List<string> Enumerate(IEnumerable<string> inputs, LibraryStorage storage, List<SkippedFile> skipped)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string file)
        {
            var full = Path.GetFullPath(file);
            if (!seen.Add(full))
                return;

            if (!InventorExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
                skipped.Add(new SkippedFile(full, "Не деталь и не сборка Inventor"));
            else if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkippedFolders.Contains))
                skipped.Add(new SkippedFile(full, "Резервная копия Inventor (OldVersions)"));
            else if (storage.Contains(full))
                skipped.Add(new SkippedFile(full, "Уже в папке библиотеки"));
            else
                result.Add(full);
        }

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var files = Directory.EnumerateFiles(input, "*.*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        // Ярлыки на папки (junction) могут зациклить обход.
                        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
                    })
                    .Where(f => InventorExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Order(NaturalComparer.Instance);
                foreach (var file in files)
                    Consider(file);
            }
            else if (File.Exists(input))
            {
                Consider(input);
            }
        }

        return result;
    }

    private async Task<ImportCandidate> AnalyzeFileAsync(string path, ImportSession session, CancellationToken ct)
    {
        var documentType = Path.GetExtension(path).Equals(".iam", StringComparison.OrdinalIgnoreCase)
            ? LibraryDocumentType.Assembly
            : LibraryDocumentType.Part;

        // Занятый или недоступный файл не должен прерывать анализ остальных.
        string hash;
        long size;
        try
        {
            (hash, size) = await LibraryStorage.HashAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось прочитать файл {Path}", path);
            var name = DesignationNormalizer.ToCanonical(Path.GetFileNameWithoutExtension(path));
            return new ImportCandidate { SourcePath = path, DocumentType = documentType, ReadError = ex.Message, OriginalDesignation = name, Designation = name };
        }

        DocumentProperties? props = null;
        string? error = null;
        if (properties.IsAvailable)
        {
            try
            {
                props = await properties.ReadAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Не удалось прочитать свойства {Path}", path);
                error = ex.Message;
            }
        }

        var referenceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in props?.References ?? [])
        {
            try
            {
                if (File.Exists(reference))
                    referenceHashes[reference] = (await LibraryStorage.HashAsync(reference, ct).ConfigureAwait(false)).Hash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Не удалось прочитать компонент сборки {Path}", reference);
            }
        }

        // Раздел спецификации уже записан в файле (например, другим плагином) — тип берём из него.
        var kindFromFile = props is null ? null : LibraryProperties.KindFromSpecSection(props.Custom);

        var candidate = new ImportCandidate
        {
            SourcePath = path,
            DocumentType = documentType,
            Properties = props,
            PropertiesError = error,
            FileHash = hash,
            FileSize = size,
            ReferenceHashes = referenceHashes,
            KindFromFile = kindFromFile is not null,
        };

        // Обозначение — из «Обозначения», если оно заполнено, иначе из имени файла.
        Prepare(candidate, session, candidate.SourceText(DesignationSource.PartNumber) is not null
            ? DesignationSource.PartNumber
            : DesignationSource.FileName, resetKind: kindFromFile is null);
        if (kindFromFile is { } kind)
            candidate.Kind = kind;
        return candidate;
    }

    /// <summary>
    /// Готовит обозначение из источника: единый вид → правила замены → единый вид. Обновляет
    /// стандарт и, если раздел не менял пользователь, предложенный раздел.
    /// </summary>
    private static void Prepare(ImportCandidate c, ImportSession session, DesignationSource source, bool resetKind)
    {
        var text = c.SourceText(source) ?? "";
        var (replaced, applied) = session.Rules.ApplyReplacements(DesignationNormalizer.ToCanonical(text));
        c.Source = source;
        c.OriginalDesignation = text;
        c.AppliedRules = applied;
        c.Designation = DesignationNormalizer.ToCanonical(replaced);
        Suggest(c, session);
        if (resetKind && !c.KindFromFile)
            c.Kind = c.Standard is not null ? LibraryItemKind.Standard : LibraryItemKind.Purchased;
    }

    /// <summary>Стандарт по обозначению (или ручной) и предлагаемый раздел — если раздел не менял пользователь.</summary>
    private static void Suggest(ImportCandidate c, ImportSession session)
    {
        var categoryUntouched = c.CategoryId == c.SuggestedCategoryId;
        c.Standard = c.ManualStandard ?? StandardParser.Find(c.Designation);
        var suggested = session.Catalog.Suggest(c.Designation, c.Standard);
        if (categoryUntouched)
            c.CategoryId = suggested?.Id;
        c.SuggestedCategoryId = suggested?.Id;
    }

}

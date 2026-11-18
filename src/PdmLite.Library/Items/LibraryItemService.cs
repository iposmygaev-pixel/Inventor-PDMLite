using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Items;

/// <summary>Компонент библиотеки для списка каталога.</summary>
public sealed record LibraryItemSummary(
    Guid Id,
    string Designation,
    LibraryItemKind Kind,
    LibraryDocumentType DocumentType,
    string? Standard,
    string? Manufacturer,
    int CategoryId,
    string CategoryPath,
    string RelativePath,
    int Version,
    DateTime ModifiedAt,
    string ModifiedBy,
    bool StandardIsManual = false);

/// <summary>Расхождение одного свойства: сейчас в файле и как должно быть.</summary>
public sealed record PropertyChange(string Property, string? Current, string Expected);

/// <summary>Результат проверки файла компонента: что изменится (пусто — всё правильно) или ошибка чтения.</summary>
public sealed record PropertyDiff(Guid ItemId, string Designation, string RelativePath, IReadOnlyList<PropertyChange> Changes, string? Error)
{
    public bool NeedsUpdate => Error is null && Changes.Count > 0;
}

/// <summary>Итог обновления свойств: проверено, записано, уже в порядке, ошибки.</summary>
public sealed record RewriteResult(int Checked, int Updated, int UpToDate, IReadOnlyList<string> Failed);

/// <summary>Компонент в группе возможных дубликатов.</summary>
/// <param name="UsedIn">В скольких сборках библиотеки он стоит.</param>
public sealed record DuplicateEntry(LibraryItemSummary Item, int UsedIn, DateTime CreatedAt);

/// <summary>Компоненты, которые, вероятно, одно и то же.</summary>
/// <param name="GeometryOnly">Совпадает только геометрия — возможно, законная копия (другое покрытие, класс прочности).</param>
public sealed record DuplicateGroup(string Reason, IReadOnlyList<DuplicateEntry> Items, bool GeometryOnly = false);

/// <summary>Новые значения карточки компонента.</summary>
/// <param name="ManualStandard">Стандарт вручную; <c>null</c> — распознаётся из обозначения.</param>
public sealed record LibraryItemEdit(string Designation, LibraryItemKind Kind, string? Manufacturer, int? CategoryId, string? ManualStandard = null);

/// <summary>Результат проверки изменения: замечания и путь файла после изменения.</summary>
public sealed record LibraryItemEditCheck(IReadOnlyList<ImportIssue> Issues, string? NewRelativePath, string CanonicalDesignation, string? Standard)
{
    public bool HasErrors => Issues.Any(i => i.Severity == ImportSeverity.Error);
}

/// <summary>Просмотр, изменение и удаление компонентов библиотеки.</summary>
/// <remarks>
/// Изменение обозначения, типа, производителя или раздела переименовывает и при необходимости
/// переносит файл, перезаписывает свойства в нём и перевязывает сборки библиотеки, которые
/// ссылаются на компонент. Сборки проектов (вне библиотеки) здесь не обновляются.
/// </remarks>
public sealed partial class LibraryItemService(
    IPdmDatabase database,
    IDocumentPropertyStore properties,
    Func<LibraryStorage> storageFactory,
    ILogger<LibraryItemService> logger)
{
    internal Func<string, PdmDbContext> CreateVerificationContext { get; init; } = LibraryMutationCommit.CreateContext;
    private Task<LibraryWriteLease> EnterWriteAsync(string root, CancellationToken ct) =>
        LibraryWriteLease.EnterAsync(database, root, ex => logger.LogWarning(ex, "Операция библиотеки: очистка ресурсов"), ct);

    public async Task<LibraryCatalog> LoadCatalogAsync(CancellationToken ct = default)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        await LibraryCatalog.SeedAsync(db, ct).ConfigureAwait(false);
        return await LibraryCatalog.LoadAsync(db, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LibraryItemSummary>> ListAsync(LibraryCatalog catalog, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var items = (await db.LibraryItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
            .OrderBy(i => i.Designation, NaturalComparer.Instance).ToList();
        return items.Select(i => Summary(i, catalog)).ToList();
    }

    /// <summary>Текущие и прежние пути компонентов — для подстановки ненайденных ссылок в сборках.</summary>
    public async Task<LibraryPathIndex> LoadPathIndexAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var items = await db.LibraryItems.AsNoTracking().Select(i => new { i.Id, i.RelativePath, i.Designation })
            .ToListAsync(ct).ConfigureAwait(false);
        var edits = await db.LibraryLog.AsNoTracking().Where(e => (e.Action == "edit" || e.Action == "merge") && e.ItemId != null)
            .ToListAsync(ct).ConfigureAwait(false);
        return new LibraryPathIndex(items.Select(i => (i.Id, i.RelativePath, i.Designation)), LibraryPathIndex.MovesFromLog(edits));
    }

    /// <summary>Обозначения сборок библиотеки, в которые входит компонент.</summary>
    public async Task<IReadOnlyList<string>> UsedInAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.LibraryItemReferences.Where(r => r.ComponentId == id)
            .Join(db.LibraryItems, r => r.AssemblyId, i => i.Id, (_, i) => i.Designation)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Проверяет изменение карточки — те же правила, что при добавлении.</summary>
    public async Task<LibraryItemEditCheck> CheckEditAsync(LibraryCatalog catalog, Guid id, LibraryItemEdit edit, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var item = await db.LibraryItems.AsNoTracking().SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        var storage = storageFactory();

        var placement = LibraryRules.Place(catalog, storage, edit.Designation, edit.Kind, edit.Manufacturer, edit.CategoryId,
            Path.GetExtension(item.RelativePath), edit.ManualStandard);
        var issues = new List<ImportIssue>();
        if (!properties.IsAvailable)
            issues.Add(new ImportIssue(ImportSeverity.Error, $"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}"));
        issues.AddRange(placement.Issues);

        if (placement.Designation.Length > 0)
        {
            var key = DesignationNormalizer.ToKey(placement.Designation);
            var other = await db.LibraryItems.AsNoTracking().Where(i => i.DesignationKey == key && i.Id != id)
                .Select(i => i.Designation).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (other is not null)
                issues.Add(new ImportIssue(ImportSeverity.Error, $"В библиотеке уже есть «{other}»."));
        }

        if (placement.RelativePath is { } newPath && !SamePath(newPath, item.RelativePath))
        {
            if (File.Exists(storage.GetFullPath(newPath)))
                issues.Add(new ImportIssue(ImportSeverity.Error, $"В папке библиотеки уже есть файл {newPath}."));

            var usedIn = await UsedInAsync(id, ct).ConfigureAwait(false);
            if (usedIn.Count > 0)
                issues.Add(new ImportIssue(ImportSeverity.Info,
                    "Сборки библиотеки, в которые входит компонент, будут перевязаны на новый файл: " + string.Join(", ", usedIn) + "."));
            issues.Add(new ImportIssue(ImportSeverity.Warning,
                "Файл будет переименован или перенесён. Если вы уже вставляли этот компонент в сборки проектов, " +
                "в них придётся заменить ссылку (автоматическое обновление — в следующих шагах)."));
        }

        return new LibraryItemEditCheck(issues, placement.RelativePath, placement.Designation, placement.Standard);
    }

    /// <summary>Применяет изменение карточки. При ошибке файл, сборки и база остаются как были.</summary>
    public async Task ApplyEditAsync(LibraryCatalog catalog, Guid id, LibraryItemEdit edit, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        var check = await CheckEditAsync(catalog, id, edit, ct).ConfigureAwait(false);
        if (check.HasErrors)
            throw new InvalidOperationException(string.Join(" ", check.Issues.Where(i => i.Severity == ImportSeverity.Error).Select(i => i.Message)));

        var user = Environment.UserName;
        var category = catalog.Get(edit.CategoryId)!;
        var manufacturer = string.IsNullOrWhiteSpace(edit.Manufacturer) ? null : edit.Manufacturer.Trim();

        await using var db = database.CreateContext();
        var item = await db.LibraryItems.Include(i => i.Versions).SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        var before = Snapshot(item, catalog.Get(item.CategoryId)?.Path);

        var oldFull = storage.GetFullPath(item.RelativePath);
        var newFull = storage.GetFullPath(check.NewRelativePath!);
        var moved = !SamePath(check.NewRelativePath!, item.RelativePath);

        // Резервные копии на время операции: свойства в файле перезаписываются, сборки перевязываются.
        var backups = new List<(string Path, string Backup)> { (oldFull, LibraryStorage.Backup(oldFull)) };
        var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
        var preserveBackups = false;
        var didMove = false;
        try
        {
            lease.SetCopies(backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
            LibraryStorage.ClearReadOnly(oldFull);
            if (moved)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
                File.Move(oldFull, newFull);
                didMove = true;
            }

            await properties.WriteAsync(newFull, LibraryProperties.For(check.CanonicalDesignation, edit.Kind, check.Standard, manufacturer, category.Path, id), ct)
                .ConfigureAwait(false);
            LibraryStorage.MakeReadOnly(newFull);
            var (hash, size) = await LibraryStorage.HashAsync(newFull, ct).ConfigureAwait(false);

            if (moved)
                await RelinkAssembliesAsync(db, storage, id, oldFull, newFull, backups, lease, ct).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var categoryChanged = item.CategoryId != category.Id;
            item.Designation = check.CanonicalDesignation;
            item.DesignationKey = DesignationNormalizer.ToKey(check.CanonicalDesignation);
            item.Kind = edit.Kind;
            item.Standard = check.Standard;
            item.StandardIsManual = edit.Kind == LibraryItemKind.Standard && LibraryRules.NormalizeStandard(edit.ManualStandard) is not null;
            item.Manufacturer = manufacturer;
            item.CategoryId = category.Id;
            item.RelativePath = check.NewRelativePath!;
            item.FileHash = hash;
            item.FileSize = size;
            item.ModifiedAt = now;
            item.ModifiedBy = user;
            if (item.Versions.FirstOrDefault(v => v.Version == item.Version) is { } current)
            {
                current.RelativePath = item.RelativePath;
                current.FileHash = hash;
                current.FileSize = size;
            }

            db.LibraryLog.Add(new LibraryLogEntry
            {
                At = now,
                User = user,
                Action = "edit",
                ItemId = id,
                Details = JsonSerializer.Serialize(new { before, after = Snapshot(item, category.Path) }),
            });
            if (categoryChanged)
                await LibraryCatalog.LearnAsync(db, item.Designation, item.Standard, category.Id, ct).ConfigureAwait(false);

            await commit.SaveAsync(db, ct).ConfigureAwait(false);

        }
        catch (Exception failure)
        {
            if (!await commit.CanRollbackAsync().ConfigureAwait(false))
            {
                preserveBackups = true;
                throw commit.Preserve(failure, backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
            }
            var errors = new List<Exception>();
            if (didMove) AttemptRollback(() => LibraryStorage.Delete(newFull), newFull, errors);
            RestoreBackups(backups, errors);
            if (errors.Count > 0)
            {
                preserveBackups = true;
                lease.Preserve("RollbackIncomplete", backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
                throw new LibraryRollbackException(failure, errors, backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
            }
            throw;
        }
        finally
        {
            if (!preserveBackups)
                foreach (var (_, backup) in backups) TryDelete(backup);
        }

        if (moved) CleanupFolders(storage, Path.GetDirectoryName(oldFull)!);
    }

    private static LibraryItemSummary Summary(LibraryItem i, LibraryCatalog catalog) =>
        new(i.Id, i.Designation, i.Kind, i.DocumentType, i.Standard, i.Manufacturer, i.CategoryId, catalog.Get(i.CategoryId)?.Path ?? "?",
            i.RelativePath, i.Version, i.ModifiedAt, i.ModifiedBy, i.StandardIsManual);

    /// <summary>Удаляет компонент: файл и запись в базе. Нельзя, если он входит в сборку библиотеки.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        var usedIn = await UsedInAsync(id, ct).ConfigureAwait(false);
        if (usedIn.Count > 0)
            throw new InvalidOperationException("Компонент входит в сборки библиотеки: " + string.Join(", ", usedIn) + ". Сначала удалите их.");

        await using var db = database.CreateContext();
        var item = await db.LibraryItems.SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        var full = storage.GetFullPath(item.RelativePath);

        db.LibraryItems.Remove(item);
        db.LibraryLog.Add(new LibraryLogEntry
        {
            At = DateTime.UtcNow,
            User = Environment.UserName,
            Action = "delete",
            ItemId = id,
            Details = JsonSerializer.Serialize(new { item.Designation, item.RelativePath }),
        });
        // До фиксации БД файл остаётся восстановимым. Занятый файл — ошибка до удаления записи.
        var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
        var staged = full + ".pdmlite-delete-" + Guid.NewGuid().ToString("N");
        var hasFile = File.Exists(full);
        lease.SetCopies(hasFile ? [new RecoveryCopy(full, staged)] : []);
        if (hasFile) File.Move(full, staged);
        try
        {
            await commit.SaveAsync(db, ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                throw commit.Preserve(failure, hasFile ? [new RecoveryCopy(full, staged)] : []);
            if (hasFile)
            {
                try { File.Move(staged, full); }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    lease.Preserve("RollbackIncomplete", [new RecoveryCopy(full, staged)]);
                    throw new AggregateException($"Не удалось вернуть файл после сбоя удаления. Копия: {staged}", failure, restore);
                }
            }
            throw;
        }
        // БД уже зафиксирована: ошибка очистки не должна выдавать удаление за неуспешное.
        TryDelete(staged);
        try { storage.RemoveEmptyFolders(Path.GetDirectoryName(full)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Не удалось убрать пустую папку после удаления {Path}", full); }
        logger.LogInformation("Компонент удалён из библиотеки: {Designation}", item.Designation);
    }

    /// <summary>Сборки библиотеки, в которые входит компонент, — на новый путь файла.</summary>
    private async Task RelinkAssembliesAsync(PdmDbContext db, LibraryStorage storage, Guid componentId, string oldFull, string newFull,
        List<(string Path, string Backup)> backups, LibraryWriteLease lease, CancellationToken ct)
    {
        var assemblies = await db.LibraryItemReferences.Where(r => r.ComponentId == componentId)
            .Join(db.LibraryItems, r => r.AssemblyId, i => i.Id, (_, i) => i).Include(i => i.Versions).ToListAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldFull] = newFull };

        foreach (var assembly in assemblies)
        {
            var path = storage.GetFullPath(assembly.RelativePath);
            backups.Add((path, LibraryStorage.Backup(path)));
            lease.SetCopies(backups.Select(b => new RecoveryCopy(b.Path, b.Backup)));
            LibraryStorage.ClearReadOnly(path);
            await properties.ReplaceReferencesAsync(path, map, ct).ConfigureAwait(false);
            var remaining = (await properties.ReadWithoutGeometryAsync(path, ct).ConfigureAwait(false)).References
                .Where(r => ReferenceKey(r) == ReferenceKey(oldFull)).ToList();
            if (remaining.Count > 0)
                throw new InvalidOperationException($"В сборке «{Path.GetFileName(path)}» не заменились ссылки: {oldFull}. Изменение отменено.");
            LibraryStorage.MakeReadOnly(path);
            (assembly.FileHash, assembly.FileSize) = await LibraryStorage.HashAsync(path, ct).ConfigureAwait(false);
            assembly.ModifiedAt = DateTime.UtcNow;
            assembly.ModifiedBy = Environment.UserName;
            if (assembly.Versions.SingleOrDefault(v => v.Version == assembly.Version) is { } current)
                (current.FileHash, current.FileSize) = (assembly.FileHash, assembly.FileSize);
        }
    }

    private static string ReferenceKey(string path) => Path.GetFullPath(path).Replace('\\', '/').ToUpperInvariant();

    /// <summary>Карточка «было/стало» для журнала.</summary>
    private sealed record ItemSnapshot(string Designation, LibraryItemKind Kind, string? Standard, string? Manufacturer, string? Category, string RelativePath);

    private static ItemSnapshot Snapshot(LibraryItem item, string? categoryPath) =>
        new(item.Designation, item.Kind, item.Standard, item.Manufacturer, categoryPath, item.RelativePath);

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void RestoreBackups(IEnumerable<(string Path, string Backup)> backups, List<Exception> errors)
    {
        foreach (var (path, backup) in backups)
            AttemptRollback(() => LibraryStorage.Restore(backup, path), path, errors);
    }
    private void AttemptRollback(Action restore, string path, List<Exception> errors)
    {
        try { restore(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Неполное восстановление {Path}", path);
            errors.Add(new IOException($"{path}: {ex.Message}", ex));
        }
    }
    private void CleanupFolders(LibraryStorage storage, string folder)
    {
        try { storage.RemoveEmptyFolders(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Изменение сохранено, но пустая папка не удалена: {Path}", folder); }
    }

    /// <summary>Удаляет файл. Не удалось — не ошибка операции, только запись в журнал.</summary>
    private void TryDelete(string path)
    {
        try
        {
            LibraryStorage.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить файл {Path}", path);
        }
    }
}

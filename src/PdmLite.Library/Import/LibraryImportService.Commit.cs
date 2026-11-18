using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Naming;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Import;

/// <summary>Внесение компонентов в библиотеку: копирование файлов, запись в базу, перевязка сборок.</summary>
public sealed partial class LibraryImportService
{
    /// <summary>
    /// Вносит готовые компоненты. Каждый — отдельно: ошибка одного не отменяет остальные,
    /// а частично скопированный файл удаляется.
    /// </summary>
    public async Task<ImportResult> CommitAsync(ImportSession session, IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        Validate(session);
        var storage = storageFactory();
        await using var lease = await LibraryWriteLease.EnterAsync(database, storage.Root,
            ex => logger.LogWarning(ex, "Импорт: очистка ресурсов"), ct).ConfigureAwait(false);
        var user = Environment.UserName;

        var eligible = session.Candidates.Where(c => c.CanImport)
            .ToDictionary(c => PathKey(c.SourcePath), StringComparer.OrdinalIgnoreCase);
        var degrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, candidate) in eligible)
        {
            var dependencies = candidate.DocumentType == LibraryDocumentType.Assembly
                ? (candidate.Properties?.References ?? []).Select(PathKey).Where(eligible.ContainsKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            degrees[path] = dependencies.Count;
            foreach (var dependency in dependencies)
            {
                if (!parents.TryGetValue(dependency, out var list)) parents[dependency] = list = [];
                list.Add(path);
            }
        }
        var ready = new Queue<string>(eligible.Keys.Where(p => degrees[p] == 0));
        var queue = new List<ImportCandidate>();
        // Kahn ordering in O(files + references), including shared nested assemblies.
        while (ready.TryDequeue(out var path))
        {
            queue.Add(eligible[path]);
            foreach (var parent in parents.GetValueOrDefault(path) ?? [])
                if (--degrees[parent] == 0) ready.Enqueue(parent);
        }
        var pending = eligible.Where(p => degrees[p.Key] > 0).Select(p => p.Value);
        var committed = new Dictionary<string, (Guid Id, string FullPath)>(StringComparer.OrdinalIgnoreCase);
        var failed = pending.Select(c => (c, "Цикл зависимостей сборок или зависимость от цикла. Импорт компонента пропущен.")).ToList();

        for (var i = 0; i < queue.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = queue[i];
            progress?.Report(new ImportProgress(i, queue.Count, c.FileName));

            string? copied = null;
            var id = Guid.NewGuid();
            var registered = false;
            var commit = new LibraryMutationCommit(lease, LibraryMutationCommit.CreateContext);
            try
            {
                var category = session.Catalog.Get(c.CategoryId)!;
                copied = storage.CopyIn(c.SourcePath, c.TargetRelativePath!);
                lease.SetCopies([new RecoveryCopy(copied, copied)]);

                await using var db = database.CreateContext();
                var componentIds = c.DocumentType == LibraryDocumentType.Assembly
                    ? await RelinkAssemblyAsync(db, storage, c, copied, committed, ct).ConfigureAwait(false)
                    : [];

                await properties.WriteAsync(copied, LibraryProperties.For(c.Designation, c.Kind, c.Standard, c.Manufacturer, category.Path, id), ct)
                    .ConfigureAwait(false);
                if (c.DocumentType == LibraryDocumentType.Assembly)
                    await VerifyAssemblyAsync(db, storage, copied, componentIds, ct).ConfigureAwait(false);
                LibraryStorage.MakeReadOnly(copied);
                var (hash, size) = await LibraryStorage.HashAsync(copied, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                var key = DesignationNormalizer.ToKey(c.Designation);

                db.LibraryItems.Add(new LibraryItem
                {
                    Id = id,
                    CategoryId = category.Id,
                    Kind = c.Kind,
                    DocumentType = c.DocumentType,
                    Designation = c.Designation,
                    DesignationKey = key,
                    Standard = c.Standard,
                    StandardIsManual = c.ManualStandard is not null && c.Kind == LibraryItemKind.Standard,
                    Manufacturer = string.IsNullOrWhiteSpace(c.Manufacturer) ? null : c.Manufacturer.Trim(),
                    RelativePath = c.TargetRelativePath!,
                    FileHash = hash,
                    FileSize = size,
                    SourceHash = c.FileHash,
                    Volume = c.Properties?.Volume,
                    Area = c.Properties?.Area,
                    Version = 1,
                    CreatedAt = now,
                    CreatedBy = user,
                    ModifiedAt = now,
                    ModifiedBy = user,
                    Versions =
                    [
                        new LibraryItemVersion
                        {
                            Version = 1, RelativePath = c.TargetRelativePath!, FileHash = hash, FileSize = size,
                            CreatedAt = now, CreatedBy = user, Comment = "Добавлен в библиотеку",
                        },
                    ],
                    References = componentIds.Distinct().Select(cid => new LibraryItemReference { AssemblyId = id, ComponentId = cid }).ToList(),
                });
                db.LibraryLog.Add(new LibraryLogEntry
                {
                    At = now,
                    User = user,
                    Action = "add",
                    ItemId = id,
                    Details = JsonSerializer.Serialize(new { source = c.SourcePath, path = c.TargetRelativePath, designation = c.Designation }),
                });
                if (c.CategoryId != c.SuggestedCategoryId)
                    await LibraryCatalog.LearnAsync(db, c.Designation, c.Standard, category.Id, ct).ConfigureAwait(false);

                await commit.SaveAsync(db, ct).ConfigureAwait(false);
                registered = true;

                committed[PathKey(c.SourcePath)] = (id, copied);
                session.Remember(key, c.Designation, c.TargetRelativePath!, c.FileHash, hash);
                if (c.Properties is { } shape && LibraryGeometry.Known(shape.Volume, shape.Area))
                    session.ExistingGeometry.Add((c.Designation, c.DocumentType, shape.Volume!.Value, shape.Area!.Value));
                ct.ThrowIfCancellationRequested();
                logger.LogInformation("В библиотеку добавлен {Designation} ({Path})", c.Designation, c.TargetRelativePath);
            }
            catch (Exception ex)
            {
                if (registered)
                {
                    committed[PathKey(c.SourcePath)] = (id, copied!);
                    if (ex is OperationCanceledException) throw;
                    logger.LogWarning(ex, "Компонент {Path} уже сохранён; его файл не удаляется", copied);
                    continue;
                }
                if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                    throw commit.Preserve(ex, copied is null ? [] : [new RecoveryCopy(copied, copied)]);
                var cleanup = copied is not null ? TryDelete(copied) : null;
                var detail = cleanup is null ? null : $"Не удалось очистить копию {copied}: {cleanup.Message}";
                if (ex is OperationCanceledException cancelled)
                {
                    if (detail is null) throw;
                    throw new OperationCanceledException($"Импорт отменён. {detail}",
                        cleanup is null ? ex : new AggregateException(ex, cleanup), cancelled.CancellationToken);
                }
                logger.LogError(ex, "Не удалось добавить {Path}", c.SourcePath);
                var message = ex is DbUpdateException { InnerException: { } inner } ? inner.Message : ex.Message;
                failed.Add((c, detail is null ? message : message + " " + detail));
            }
        }

        progress?.Report(new ImportProgress(queue.Count, queue.Count, null));
        return new ImportResult(committed.Count, failed)
        {
            Committed = session.Candidates.Where(c => committed.ContainsKey(PathKey(c.SourcePath)))
                .ToDictionary(c => c.SourcePath, c => committed[PathKey(c.SourcePath)].FullPath, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Переводит ссылки скопированной сборки на файлы библиотеки; возвращает Id компонентов.</summary>
    private async Task<List<Guid>> RelinkAssemblyAsync(PdmDbContext db, LibraryStorage storage, ImportCandidate c, string copied,
        Dictionary<string, (Guid Id, string FullPath)> committed, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<Guid>();
        foreach (var reference in c.Properties?.References ?? [])
        {
            if (committed.TryGetValue(PathKey(reference), out var done))
            {
                map[reference] = done.FullPath;
                ids.Add(done.Id);
            }
            else if (storage.Contains(reference))
            {
                var relative = Path.GetRelativePath(storage.Root, reference).Replace(Path.DirectorySeparatorChar, '/');
                var item = await db.LibraryItems.AsNoTracking().FirstOrDefaultAsync(x => x.RelativePath == relative, ct).ConfigureAwait(false)
                           ?? throw new InvalidOperationException($"Файл {relative} лежит в папке библиотеки, но не зарегистрирован в ней.");
                ids.Add(item.Id);
            }
            else if (c.ReferenceHashes.TryGetValue(reference, out var hash)
                     && await db.LibraryItems.AsNoTracking().FirstOrDefaultAsync(x => x.SourceHash == hash || x.FileHash == hash, ct).ConfigureAwait(false) is { } known)
            {
                // Деталь внесли раньше из этого же файла — сборка будет ссылаться на её копию в библиотеке.
                map[reference] = storage.GetFullPath(known.RelativePath);
                ids.Add(known.Id);
            }
            else
            {
                throw new InvalidOperationException($"Компонент сборки «{Path.GetFileName(reference)}» не внесён в библиотеку.");
            }
        }

        if (map.Count > 0)
            await properties.ReplaceReferencesAsync(copied, map, ct).ConfigureAwait(false);
        return ids;
    }

    private static string PathKey(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private async Task VerifyAssemblyAsync(PdmDbContext db, LibraryStorage storage, string copied,
        List<Guid> componentIds, CancellationToken ct)
    {
        var expected = (await db.LibraryItems.AsNoTracking().Where(i => componentIds.Contains(i.Id))
            .Select(i => i.RelativePath).ToListAsync(ct).ConfigureAwait(false))
            .Select(p => PathKey(storage.GetFullPath(p))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = (await properties.ReadWithoutGeometryAsync(copied, ct).ConfigureAwait(false))
            .References.Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(actual) || expected.Any(p => !File.Exists(p)))
            throw new InvalidOperationException("Не подтверждена замена ссылок сборки. "
                + "Отсутствуют: " + string.Join("; ", expected.Except(actual, StringComparer.OrdinalIgnoreCase))
                + ". Неожиданные ссылки: " + string.Join("; ", actual.Except(expected, StringComparer.OrdinalIgnoreCase)));
    }

    private Exception? TryDelete(string path)
    {
        try
        {
            LibraryStorage.Delete(path);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Путь остался после удаления.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить {Path} после ошибки", path);
            return ex;
        }
    }
}

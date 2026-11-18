using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Items;

/// <summary>Обслуживание: проверка и перезапись свойств PdmLite в файлах библиотеки.</summary>
public sealed partial class LibraryItemService
{
    /// <summary>
    /// Шаг 1 обслуживания — только чтение: какие свойства PdmLite в файлах расходятся с базой
    /// (например, нет раздела спецификации, осталась метка Content Center). Файлы не меняются.
    /// </summary>
    /// <param name="ids">Какие компоненты проверить; <c>null</c> — вся библиотека.</param>
    public async Task<IReadOnlyList<PropertyDiff>> DiagnosePropertiesAsync(LibraryCatalog catalog, IReadOnlyCollection<Guid>? ids,
        IProgress<(int Done, int Total, string? Current)>? progress = null, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await _files.EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        if (!properties.IsAvailable)
            throw new InvalidOperationException($"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}");

        var items = await LoadItemsAsync(ids, ct).ConfigureAwait(false);
        var result = new List<PropertyDiff>();

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            progress?.Report((i, items.Count, item.Designation));
            var full = storage.GetFullPath(item.RelativePath);
            try
            {
                if (!File.Exists(full))
                    throw new FileNotFoundException("Файл не найден в папке библиотеки.", full);
                var current = await properties.ReadAsync(full, ct).ConfigureAwait(false);
                if (!LibraryGeometry.Known(item.Volume, item.Area) && LibraryGeometry.Known(current.Volume, current.Area))
                    await SaveGeometryAsync(item.Id, current.Volume!.Value, current.Area!.Value, ct).ConfigureAwait(false);
                result.Add(new PropertyDiff(item.Id, item.Designation, item.RelativePath, Compare(current, Expected(item, catalog)), null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Add(new PropertyDiff(item.Id, item.Designation, item.RelativePath, [], ex.Message));
            }
        }

        progress?.Report((items.Count, items.Count, null));
        return result;
    }

    /// <summary>
    /// Шаг 2 обслуживания — запись свойств PdmLite в указанные файлы библиотеки.
    /// </summary>
    /// <remarks>
    /// Безопасность на большой библиотеке: перед записью каждый файл проверяется заново и не
    /// изменяется, если свойства уже правильные; перед записью — резервная копия файла, при ошибке
    /// файл восстанавливается, остальные продолжаются; база (контрольная сумма) обновляется после
    /// каждого файла; остановка — между файлами.
    /// </remarks>
    public async Task<RewriteResult> RewritePropertiesAsync(LibraryCatalog catalog, IReadOnlyCollection<Guid>? ids,
        IProgress<(int Done, int Total, string? Current)>? progress = null, CancellationToken ct = default)
    {
        if (!properties.IsAvailable)
            throw new InvalidOperationException($"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}");
        var storage = storageFactory();
        await using var lease = await _files.EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        var snapshots = await LoadItemsAsync(ids, ct).ConfigureAwait(false);
        var failed = new List<string>();
        int updated = 0, upToDate = 0, done = 0;
        try
        {
            foreach (var snapshot in snapshots)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((done, snapshots.Count, snapshot.Designation));
                var full = storage.GetFullPath(snapshot.RelativePath);
                string? backup = null;
                var preserve = false;
                var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
                await using var db = database.CreateContext();
                try
                {
                    var item = await db.LibraryItems.Include(i => i.Versions).SingleAsync(i => i.Id == snapshot.Id, ct).ConfigureAwait(false);
                    if (!File.Exists(full)) throw new FileNotFoundException("Файл не найден в папке библиотеки.", full);
                    var expected = Expected(item, catalog);
                    var change = Compare(await properties.ReadAsync(full, ct).ConfigureAwait(false), expected).Count > 0;
                    if (change)
                    {
                        backup = LibraryStorage.Backup(full);
                        lease.SetCopies([new RecoveryCopy(full, backup)]);
                        LibraryStorage.ClearReadOnly(full);
                        await properties.WriteAsync(full, expected, ct).ConfigureAwait(false);
                        LibraryStorage.MakeReadOnly(full);
                    }
                    // Совпадение свойств не означает, что сохранённая контрольная сумма актуальна.
                    var (hash, size) = await LibraryStorage.HashAsync(full, ct).ConfigureAwait(false);
                    var version = item.Versions.FirstOrDefault(v => v.Version == item.Version);
                    if (!change && item.FileHash == hash && item.FileSize == size
                        && (version is null || version.FileHash == hash && version.FileSize == size))
                        upToDate++;
                    else
                    {
                        (item.FileHash, item.FileSize) = (hash, size);
                        if (version is not null) (version.FileHash, version.FileSize) = (hash, size);
                        db.LibraryLog.Add(new LibraryLogEntry { At = DateTime.UtcNow, User = Environment.UserName,
                            Action = "rewrite-properties-file", ItemId = item.Id, Details = JsonSerializer.Serialize(new { item.RelativePath, hash, size }) });
                        await commit.SaveAsync(db, ct).ConfigureAwait(false);
                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    var copies = new[] { new RecoveryCopy(full, backup ?? full) };
                    if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                    {
                        preserve = true;
                        throw commit.Preserve(ex, copies);
                    }
                    if (backup is not null)
                    {
                        var errors = new List<Exception>();
                        _files.RestoreBackups([(full, backup)], errors);
                        if (errors.Count > 0)
                        {
                            preserve = true;
                            lease.Preserve("RollbackIncomplete", copies);
                            throw new LibraryRollbackException(ex, errors, copies);
                        }
                    }
                    if (ex is OperationCanceledException) throw;
                    logger.LogWarning(ex, "Не удалось обновить свойства {Path}", full);
                    failed.Add($"{snapshot.Designation}: {ex.Message}");
                }
                finally { if (backup is not null && !preserve) _files.TryDelete(backup); }
                done++;
            }
        }
        finally
        {
            if (!lease.IsPreserved)
            {
                try
                {
                    await using var log = database.CreateContext();
                    log.LibraryLog.Add(new LibraryLogEntry { At = DateTime.UtcNow, User = Environment.UserName,
                        Action = "rewrite-properties", Details = JsonSerializer.Serialize(new { total = snapshots.Count, done, updated, upToDate, failed = failed.Count }) });
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await log.SaveChangesAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Не удалось сохранить итог обслуживания свойств"); }
            }
        }
        progress?.Report((snapshots.Count, snapshots.Count, null));
        return new RewriteResult(snapshots.Count, updated, upToDate, failed);
    }

    private async Task<List<LibraryItem>> LoadItemsAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        var query = db.LibraryItems.AsNoTracking();
        if (ids is not null)
            query = query.Where(i => ids.Contains(i.Id));
        return (await query.ToListAsync(ct).ConfigureAwait(false)).OrderBy(i => i.Designation, NaturalComparer.Instance).ToList();
    }

    private static DocumentPropertyChanges Expected(LibraryItem item, LibraryCatalog catalog) =>
        LibraryProperties.For(item.Designation, item.Kind, item.Standard, item.Manufacturer, catalog.Get(item.CategoryId)?.Path ?? "", item.Id);

    /// <summary>Что изменится в файле: свойство, сейчас, станет. Пусто — свойства уже правильные.</summary>
    internal static IReadOnlyList<PropertyChange> Compare(DocumentProperties current, DocumentPropertyChanges expected)
    {
        var changes = new List<PropertyChange>();
        void Check(string name, string? now, string? target)
        {
            if (target is not null && now != target)
                changes.Add(new PropertyChange(name, now, target));
        }

        Check("Обозначение", current.PartNumber, expected.PartNumber);
        Check("Описание", current.Description, expected.Description);
        Check("Наименование", current.Title, expected.Title);
        foreach (var (name, value) in expected.Custom)
            Check(name, current.Custom.TryGetValue(name, out var now) ? now : null, value);
        if (expected.RemoveContentCenterLink && current.IsContentCenter)
            changes.Add(new PropertyChange("Метка Content Center", "есть", "снята"));
        return changes;
    }

    /// <summary>Запоминает геометрию компонента (файл не меняется) — для поиска дубликатов.</summary>
    private async Task SaveGeometryAsync(Guid id, double volume, double area, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        var item = await db.LibraryItems.SingleAsync(i => i.Id == id, ct).ConfigureAwait(false);
        item.Volume = volume;
        item.Area = area;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

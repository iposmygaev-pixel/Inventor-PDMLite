using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Naming;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Designations;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Items;

/// <summary>Копирование компонента, поиск и объединение дубликатов.</summary>
public sealed partial class LibraryItemService
{
    // ---------- Дублирование ----------

    /// <summary>Проверка копии компонента с новыми значениями (обозначение должно отличаться).</summary>
    public async Task<LibraryItemEditCheck> CheckDuplicateAsync(LibraryCatalog catalog, Guid sourceId, LibraryItemEdit edit, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var source = await db.LibraryItems.AsNoTracking().SingleAsync(i => i.Id == sourceId, ct).ConfigureAwait(false);
        var storage = storageFactory();
        var placement = LibraryRules.Place(catalog, storage, edit.Designation, edit.Kind, edit.Manufacturer, edit.CategoryId,
            Path.GetExtension(source.RelativePath), edit.ManualStandard);

        var issues = new List<ImportIssue>();
        if (!properties.IsAvailable)
            issues.Add(new ImportIssue(ImportSeverity.Error, $"Нет доступа к свойствам файлов Inventor: {properties.UnavailableReason}"));
        issues.AddRange(placement.Issues);
        if (placement.Designation.Length > 0)
        {
            var key = DesignationNormalizer.ToKey(placement.Designation);
            var other = await db.LibraryItems.AsNoTracking().Where(i => i.DesignationKey == key)
                .Select(i => i.Designation).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (other is not null)
                issues.Add(new ImportIssue(ImportSeverity.Error,
                    key == source.DesignationKey ? "Измените обозначение: у копии оно должно быть своё." : $"В библиотеке уже есть «{other}»."));
        }
        if (placement.RelativePath is { } path && File.Exists(storage.GetFullPath(path)))
            issues.Add(new ImportIssue(ImportSeverity.Error, $"В папке библиотеки уже есть файл {path}."));
        if (!File.Exists(storage.GetFullPath(source.RelativePath)))
            issues.Add(new ImportIssue(ImportSeverity.Error, $"Нет файла исходного компонента: {source.RelativePath}."));

        return new LibraryItemEditCheck(issues, placement.RelativePath, placement.Designation, placement.Standard);
    }

    /// <summary>
    /// Копия компонента (тот же файл — те же размеры) с новым обозначением и свойствами, например
    /// с другим покрытием. Возвращает Id нового компонента.
    /// </summary>
    public async Task<Guid> DuplicateAsync(LibraryCatalog catalog, Guid sourceId, LibraryItemEdit edit, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        var check = await CheckDuplicateAsync(catalog, sourceId, edit, ct).ConfigureAwait(false);
        if (check.HasErrors)
            throw new InvalidOperationException(string.Join(" ", check.Issues.Where(i => i.Severity == ImportSeverity.Error).Select(i => i.Message)));

        var user = Environment.UserName;
        var category = catalog.Get(edit.CategoryId)!;
        var manufacturer = string.IsNullOrWhiteSpace(edit.Manufacturer) ? null : edit.Manufacturer.Trim();

        await using var db = database.CreateContext();
        var source = await db.LibraryItems.AsNoTracking().SingleAsync(i => i.Id == sourceId, ct).ConfigureAwait(false);
        var componentIds = await db.LibraryItemReferences.Where(r => r.AssemblyId == sourceId).Select(r => r.ComponentId)
            .ToListAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid();
        var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
        var copied = storage.CopyIn(storage.GetFullPath(source.RelativePath), check.NewRelativePath!);
        try
        {
            lease.SetCopies([new RecoveryCopy(copied, copied)]);
            LibraryStorage.ClearReadOnly(copied);
            await properties.WriteAsync(copied, LibraryProperties.For(check.CanonicalDesignation, edit.Kind, check.Standard, manufacturer, category.Path, id), ct)
                .ConfigureAwait(false);
            LibraryStorage.MakeReadOnly(copied);
            var (hash, size) = await LibraryStorage.HashAsync(copied, ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;

            db.LibraryItems.Add(new LibraryItem
            {
                Id = id,
                CategoryId = category.Id,
                Kind = edit.Kind,
                DocumentType = source.DocumentType,
                Designation = check.CanonicalDesignation,
                DesignationKey = DesignationNormalizer.ToKey(check.CanonicalDesignation),
                Standard = check.Standard,
                StandardIsManual = edit.Kind == LibraryItemKind.Standard && LibraryRules.NormalizeStandard(edit.ManualStandard) is not null,
                Manufacturer = manufacturer,
                RelativePath = check.NewRelativePath!,
                FileHash = hash,
                FileSize = size,
                Volume = source.Volume,
                Area = source.Area,
                Version = 1,
                CreatedAt = now,
                CreatedBy = user,
                ModifiedAt = now,
                ModifiedBy = user,
                Versions =
                [
                    new LibraryItemVersion
                    {
                        Version = 1, RelativePath = check.NewRelativePath!, FileHash = hash, FileSize = size,
                        CreatedAt = now, CreatedBy = user, Comment = $"Копия «{source.Designation}»",
                    },
                ],
                References = componentIds.Select(cid => new LibraryItemReference { AssemblyId = id, ComponentId = cid }).ToList(),
            });
            db.LibraryLog.Add(new LibraryLogEntry
            {
                At = now,
                User = user,
                Action = "duplicate",
                ItemId = id,
                Details = JsonSerializer.Serialize(new { from = source.Designation, fromId = source.Id, designation = check.CanonicalDesignation, path = check.NewRelativePath }),
            });
            await commit.SaveAsync(db, ct).ConfigureAwait(false);

        }
        catch (Exception failure)
        {
            if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                throw commit.Preserve(failure, [new RecoveryCopy(copied, copied)]);
            TryDelete(copied);
            throw;
        }
        logger.LogInformation("Компонент скопирован: {From} → {To}", source.Designation, check.CanonicalDesignation);
        return id;
    }

    // ---------- Дубликаты ----------

    /// <summary>
    /// Ищет компоненты, которые, вероятно, одно и то же: одинаковый файл или обозначения, отличающиеся
    /// только пробелами, знаками препинания и регистром. Только чтение.
    /// </summary>
    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(LibraryCatalog catalog, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        var items = await db.LibraryItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var usedIn = (await db.LibraryItemReferences.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(r => r.ComponentId).ToDictionary(g => g.Key, g => g.Count());

        // Объединение в группы по любому из признаков (система непересекающихся множеств).
        var parent = items.ToDictionary(i => i.Id, i => i.Id);
        Guid Root(Guid id) => parent[id] == id ? id : parent[id] = Root(parent[id]);
        var reasons = new Dictionary<Guid, HashSet<string>>();
        void Join(IEnumerable<LibraryItem> same, string reason)
        {
            var list = same.ToList();
            if (list.Count < 2)
                return;
            foreach (var other in list.Skip(1))
                parent[Root(other.Id)] = Root(list[0].Id);
            foreach (var item in list)
                (reasons.TryGetValue(item.Id, out var r) ? r : reasons[item.Id] = []).Add(reason);
        }

        foreach (var group in items.GroupBy(i => LooseKey(i.Designation)).Where(g => g.Key.Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            Join(group, "обозначения отличаются только пробелами, знаками или регистром");
        }
        var byHash = items.SelectMany(i => new[] { i.FileHash, i.SourceHash }.OfType<string>().Distinct().Select(h => (Hash: h, Item: i)))
            .GroupBy(x => x.Hash);
        foreach (var group in byHash)
        {
            ct.ThrowIfCancellationRequested();
            Join(group.Select(x => x.Item).DistinctBy(i => i.Id), "одинаковый файл");
        }

        const string sameShape = "одинаковая геометрия (объём и площадь поверхности)";
        foreach (var (first, second) in LibraryGeometry.MatchingPairs(
                     items.Select(i => (i, (int)i.DocumentType, i.Volume, i.Area)), ct))
            Join([first, second], sameShape);
        ct.ThrowIfCancellationRequested();

        return items.Where(i => reasons.ContainsKey(i.Id))
            .GroupBy(i => Root(i.Id))
            .Select(g =>
            {
                var groupReasons = g.SelectMany(i => reasons[i.Id]).Distinct().ToList();
                return new DuplicateGroup(
                    string.Join("; ", groupReasons),
                    g.OrderBy(i => i.Designation, NaturalComparer.Instance)
                        .Select(i => new DuplicateEntry(Summary(i, catalog), usedIn.GetValueOrDefault(i.Id), i.CreatedAt)).ToList(),
                    groupReasons is [sameShape]);
            })
            .OrderBy(g => g.Items[0].Item.Designation, NaturalComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// Ключ для поиска дубликатов: без пробелов, регистра и знаков препинания — кроме знака между
    /// двумя числами: он разделяет их («1,6» ≠ «16», «20.109» ≠ «2010.9»), поэтому заменяется точкой.
    /// </summary>
    public static string LooseKey(string designation)
    {
        var key = DesignationNormalizer.ToKey(designation);
        var sb = new System.Text.StringBuilder(key.Length);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                continue;
            }

            // Знаки подряд («-.») — один разделитель; ставим его, только если он между цифрами.
            var next = i + 1;
            while (next < key.Length && !char.IsLetterOrDigit(key[next]))
                next++;
            if (sb.Length > 0 && char.IsDigit(sb[^1]) && next < key.Length && char.IsDigit(key[next]))
                sb.Append('.');
            i = next - 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Объединяет дубликаты: сборки библиотеки перевязываются на оставляемый компонент, лишние
    /// удаляются (файл и запись). Путь удалённого запоминается — сборки проектов, где он стоял,
    /// при открытии в Inventor получат оставленный компонент.
    /// </summary>
    public async Task MergeDuplicatesAsync(Guid keepId, IReadOnlyCollection<Guid> removeIds, CancellationToken ct = default)
    {
        var storage = storageFactory();
        await using var lease = await EnterWriteAsync(storage.Root, ct).ConfigureAwait(false);
        if (removeIds.Contains(keepId))
            throw new ArgumentException("Оставляемый компонент не может быть в списке удаляемых.", nameof(removeIds));

        foreach (var removeId in removeIds)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = database.CreateContext();
            var keep = await db.LibraryItems.SingleAsync(i => i.Id == keepId, ct).ConfigureAwait(false);
            var remove = await db.LibraryItems.SingleAsync(i => i.Id == removeId, ct).ConfigureAwait(false);
            if (keep.DocumentType != remove.DocumentType)
                throw new InvalidOperationException($"«{remove.Designation}» и «{keep.Designation}»: деталь нельзя объединить со сборкой.");

            var keepFull = storage.GetFullPath(keep.RelativePath);
            var removeFull = storage.GetFullPath(remove.RelativePath);
            if (!File.Exists(keepFull))
                throw new InvalidOperationException($"Нет файла оставляемого компонента: {keep.RelativePath}.");

            var backups = new List<(string Path, string Backup)>();
            string? staged = null;
            var preserveBackups = false;
            var commit = new LibraryMutationCommit(lease, CreateVerificationContext);
            try
            {
                // Сборки библиотеки: ссылки в файлах и состав в базе — на оставляемый компонент.
                await RelinkAssembliesAsync(db, storage, removeId, removeFull, keepFull, backups, lease, ct).ConfigureAwait(false);
                var references = await db.LibraryItemReferences.Where(r => r.ComponentId == removeId).ToListAsync(ct).ConfigureAwait(false);
                foreach (var reference in references)
                {
                    db.LibraryItemReferences.Remove(reference);
                    if (!await db.LibraryItemReferences.AnyAsync(r => r.AssemblyId == reference.AssemblyId && r.ComponentId == keepId, ct).ConfigureAwait(false))
                        db.LibraryItemReferences.Add(new LibraryItemReference { AssemblyId = reference.AssemblyId, ComponentId = keepId });
                }

                db.LibraryItems.Remove(remove);
                db.LibraryLog.Add(new LibraryLogEntry
                {
                    At = DateTime.UtcNow,
                    User = Environment.UserName,
                    Action = "merge",
                    ItemId = keepId,
                    Details = JsonSerializer.Serialize(new { remove.Designation, remove.RelativePath, mergedId = removeId, into = keep.Designation }),
                });
                if (File.Exists(removeFull) && !SamePath(removeFull, keepFull))
                {
                    staged = removeFull + ".pdmlite-merge-" + Guid.NewGuid().ToString("N");
                    lease.SetCopies(backups.Select(b => new RecoveryCopy(b.Path, b.Backup)).Append(new RecoveryCopy(removeFull, staged)));
                    File.Move(removeFull, staged);
                }
                await commit.SaveAsync(db, ct).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                var available = backups.Select(b => new RecoveryCopy(b.Path, b.Backup)).ToList();
                if (staged is not null) available.Add(new RecoveryCopy(removeFull, staged));
                if (!await commit.CanRollbackAsync().ConfigureAwait(false))
                {
                    preserveBackups = true;
                    throw commit.Preserve(failure, available);
                }
                var errors = new List<Exception>();
                RestoreBackups(backups, errors);
                if (staged is not null && File.Exists(staged))
                    AttemptRollback(() => File.Move(staged, removeFull), removeFull, errors);
                if (errors.Count > 0)
                {
                    preserveBackups = true;
                    var copies = backups.Select(b => new RecoveryCopy(b.Path, b.Backup)).ToList();
                    if (staged is not null && File.Exists(staged)) copies.Add(new(removeFull, staged));
                    lease.Preserve("RollbackIncomplete", copies);
                    throw new LibraryRollbackException(failure, errors, copies);
                }
                throw;
            }
            finally
            {
                if (!preserveBackups)
                    foreach (var (_, backup) in backups) TryDelete(backup);
            }

            if (staged is not null) TryDelete(staged);
            try { storage.RemoveEmptyFolders(Path.GetDirectoryName(removeFull)!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogWarning(ex, "Не удалось убрать пустую папку после объединения {Path}", removeFull); }
            logger.LogInformation("Дубликат {Removed} объединён с {Kept}", remove.Designation, keep.Designation);
        }
    }
}

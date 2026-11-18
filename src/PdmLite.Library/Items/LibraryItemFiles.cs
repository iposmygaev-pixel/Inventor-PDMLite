using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Items;

/// <summary>
/// Общие файловые операции изменения компонентов библиотеки: блокировка записи, перевязка сборок,
/// откат копий, очистка. Используются <see cref="LibraryItemService"/> и <see cref="LibraryDuplicateService"/>.
/// </summary>
internal sealed class LibraryItemFiles(IPdmDatabase database, IDocumentPropertyStore properties, ILogger logger)
{
    internal Task<LibraryWriteLease> EnterWriteAsync(string root, CancellationToken ct) =>
        LibraryWriteLease.EnterAsync(database, root, ex => logger.LogWarning(ex, "Операция библиотеки: очистка ресурсов"), ct);

    /// <summary>Сборки библиотеки, в которые входит компонент, — на новый путь файла.</summary>
    internal async Task RelinkAssembliesAsync(PdmDbContext db, LibraryStorage storage, Guid componentId, string oldFull, string newFull,
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

    internal static string ReferenceKey(string path) => Path.GetFullPath(path).Replace('\\', '/').ToUpperInvariant();

    internal void RestoreBackups(IEnumerable<(string Path, string Backup)> backups, List<Exception> errors)
    {
        foreach (var (path, backup) in backups)
            AttemptRollback(() => LibraryStorage.Restore(backup, path), path, errors);
    }

    internal void AttemptRollback(Action restore, string path, List<Exception> errors)
    {
        try { restore(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Неполное восстановление {Path}", path);
            errors.Add(new IOException($"{path}: {ex.Message}", ex));
        }
    }

    internal void CleanupFolders(LibraryStorage storage, string folder)
    {
        try { storage.RemoveEmptyFolders(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Изменение сохранено, но пустая папка не удалена: {Path}", folder); }
    }

    /// <summary>Удаляет файл. Не удалось — не ошибка операции, только запись в журнал.</summary>
    internal void TryDelete(string path)
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

using Npgsql;
using PdmLite.Library.Items;
using System.Data;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Data.Library;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Backup;

/// <summary>Восстановление библиотеки из резервной копии с полной заменой текущей.</summary>
public sealed partial class LibraryBackupService
{
    // ---------- Восстановление ----------

    /// <summary>Только чтение: что изменится при восстановлении копии.</summary>
    public async Task<RestorePlan> PlanRestoreAsync(string path, CancellationToken ct = default)
    {
        var info = Read(path) ?? throw new InvalidDataException("Файл не является резервной копией PdmLite.");
        if (info.Manifest.Format != BackupManifest.CurrentFormat)
            throw new InvalidDataException("Формат резервной копии не поддерживается этой версией PdmLite.");

        var (snapshot, _) = ReadArchive(path);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        var now = await db.LibraryItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var nowById = now.ToDictionary(i => i.Id);
        var backupById = snapshot.Items.ToDictionary(i => i.Id);

        var added = snapshot.Items.Where(i => !nowById.ContainsKey(i.Id)).Select(i => i.Designation).ToList();
        var removed = now.Where(i => !backupById.ContainsKey(i.Id)).Select(i => i.Designation).ToList();
        var changed = snapshot.Items.Where(i => nowById.TryGetValue(i.Id, out var n)
                && (n.Designation != i.Designation || n.RelativePath != i.RelativePath || n.FileHash != i.FileHash))
            .Select(i => nowById[i.Id].Designation == i.Designation ? i.Designation : $"{nowById[i.Id].Designation} → {i.Designation}")
            .ToList();
        var missing = await VerifyFilesAsync(path, snapshot, info.Manifest, ct).ConfigureAwait(false);

        var storage = storageFactory();
        var filesNow = Directory.Exists(storage.Root) ? Directory.EnumerateFiles(storage.Root, "*", SearchOption.AllDirectories).Count() : 0;
        return new RestorePlan(info, added, removed, changed, now.Count, filesNow, missing);
    }

    /// <summary>
    /// Восстанавливает копию: сначала копия текущего состояния, затем файлы распаковываются рядом,
    /// папка библиотеки подменяется, база заменяется в одной транзакции. При ошибке — откат.
    /// </summary>
    /// <returns>Копия, сделанная перед восстановлением.</returns>
    public async Task<BackupInfo> RestoreAsync(string path, IProgress<(int Done, int Total, string? Current)>? progress = null,
        CancellationToken ct = default)
    {
        // Проверяем и распаковываем только частную копию. Исходный ZIP может быть
        // изменён или удалён (в том числе Prune) во время BeforeRestore.
        var work = Path.Combine(Path.GetTempPath(), "pdmlite-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var pinned = Path.Combine(work, "archive.zip");
        try
        {
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var target = new FileStream(pinned, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            await using var pinnedReadLock = new FileStream(pinned, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await RestorePinnedAsync(pinned, path, progress, ct).ConfigureAwait(false);
        }
        finally { TryDeleteFolder(work); }
    }

    private async Task<BackupInfo> RestorePinnedAsync(string path, string originalPath,
        IProgress<(int Done, int Total, string? Current)>? progress, CancellationToken ct)
    {
        var root = Path.TrimEndingDirectorySeparator(storageFactory().Root);
        LibraryWriteLease.CheckPending(root, restoring: true);
        var stamp = Guid.NewGuid().ToString("N");
        var staging = root + ".restore-" + stamp;
        var previous = root + ".before-restore-" + stamp;
        var failed = root + ".failed-restore-" + stamp;
        using var recovery = new RestoreRecoveryJournal(root, stamp, previous,
            ex => logger.LogError(ex, "Не удалось обновить журнал восстановления {Root}", root));
        var plan = await PlanRestoreAsync(path, ct).ConfigureAwait(false);
        if (plan.IsDamaged)
            throw new InvalidDataException("Копия повреждена: " + string.Join("; ", plan.MissingFiles.Take(5)) + ".");

        var gatedConnectionString = database.Settings.BuildConnectionString();
        recovery.SetTarget(gatedConnectionString);
        await using var databaseGate = await RestoreDatabaseGate.EnterAsync(gatedConnectionString, VerificationTimeout,
            ex => logger.LogWarning(ex, "Не удалось освободить блокировку восстановления"), ct).ConfigureAwait(false);
        LibraryWriteLease.CheckPending(root, restoring: true);
        var before = await CreateCoreAsync(BackupReason.BeforeRestore, progress, ct, recovery.SetBackup).ConfigureAwait(false);
        var (snapshot, _) = ReadArchive(path);
        var marker = JsonSerializer.Serialize(new { operationId = stamp, backup = Path.GetFileName(originalPath), items = snapshot.Items.Count });
        var operation = new RestoreDatabaseAttempt(BitConverter.ToInt64(Guid.ParseExact(stamp, "N").ToByteArray(), 0), marker, gatedConnectionString);

        // 1. Файлы — во временную папку рядом с библиотекой (тот же диск: подмена — переименованием).
        try
        {
            Extract(path, staging, snapshot, progress, ct);
        }
        catch
        {
            TryDeleteFolder(staging);
            throw;
        }

        // 2. Подмена папки. Занята (файлы открыты в Inventor) — ничего не меняем.
        var hadRoot = Directory.Exists(root);
        try
        {
            if (hadRoot)
                MoveDirectory(root, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(staging);
            throw new InvalidOperationException(
                "Папка библиотеки занята — закройте в Inventor документы из библиотеки (или Inventor) и повторите. " + ex.Message, ex);
        }

        var staged = false;
        try
        {
            MoveDirectory(staging, root);
            staged = true;
            await ReplaceDatabaseAsync(snapshot, operation, ct).ConfigureAwait(false);
            await databaseGate.EnsureHeldAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var outcome = RestoreDatabaseOutcome.NotCommitted;
            Exception? verificationError = null;
            if (operation.CommitStarted)
            {
                try
                {
                    await databaseGate.EnsureHeldAsync().ConfigureAwait(false);
                    (outcome, verificationError) = await VerifyRestoreCommitAsync(operation.LockKey, marker, operation.ConnectionString).ConfigureAwait(false);
                    await databaseGate.EnsureHeldAsync().ConfigureAwait(false);
                }
                catch (Exception gateError) { outcome = RestoreDatabaseOutcome.Unknown; verificationError = gateError; }
            }
            if (outcome == RestoreDatabaseOutcome.Committed)
            {
                logger.LogWarning(ex, "Фиксация восстановления {Operation} подтверждена повторной проверкой БД", stamp);
                // Восстановленные файлы соответствуют подтверждённой БД. Файловый откат запрещён.
            }
            else if (outcome == RestoreDatabaseOutcome.Unknown)
            {
                recovery.Preserve("CommitUnknown");
                var unknown = new LibraryRestoreException(ex, [], root, previous, root, before.Path,
                    outcome, false, verificationError, recovery.Path);
                logger.LogCritical(unknown, "Результат фиксации восстановления {Operation} неизвестен; обе папки сохранены", stamp);
                throw unknown;
            }
            else
            {
                var rollbackErrors = RestoreRollback.Run(root, previous, failed, hadRoot, staged, MoveDirectory);
                // Очистка больше не является условием возврата исходных файлов.
                // При неполном откате не трогаем ни одну из аварийных папок.
                if (rollbackErrors.Count == 0)
                    TryDeleteFolder(staged ? failed : staging);
                if (rollbackErrors.Count > 0) recovery.Preserve("RollbackIncomplete");
                var failure = new LibraryRestoreException(ex, rollbackErrors, root, previous,
                    staged ? failed : staging, before.Path, recoveryJournal: recovery.Path);
                if (!failure.RollbackCompleted)
                    logger.LogCritical(failure, "Откат восстановления библиотеки не завершён; исходные данные: {Previous}; копия: {Backup}", previous, before.Path);
                throw failure;
            }
        }

        if (hadRoot)
            TryDeleteFolder(previous);
        logger.LogInformation("Библиотека восстановлена из {Path}; копия перед восстановлением: {Before}", originalPath, before.Path);
        return before;
    }
}

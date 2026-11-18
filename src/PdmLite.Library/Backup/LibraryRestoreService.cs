using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using PdmLite.Core.IO;
using PdmLite.Data.Library;
using PdmLite.Data;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Backup;

/// <summary>
/// Восстановление библиотеки из резервной копии с полной заменой текущей: план «что изменится»,
/// копия текущего состояния, подмена папки, замена базы в одной транзакции и откат при ошибке.
/// </summary>
/// <remarks>
/// Копию текущего состояния перед восстановлением делает <see cref="LibraryBackupService"/>; база и папка
/// библиотеки берутся у него же, чтобы копия и восстановление не могли относиться к разным библиотекам.
/// </remarks>
public sealed class LibraryRestoreService(LibraryBackupService backups, ILogger<LibraryRestoreService> logger)
{
    private readonly IPdmDatabase database = backups.Database;
    private readonly Func<LibraryStorage> storageFactory = backups.StorageFactory;

    internal Action<string, string> MoveDirectory { get; init; } = Directory.Move;
    internal Action<string> DeleteDirectory { get; init; } = path => Directory.Delete(path, recursive: true);

    internal Func<string, PdmDbContext> CreateVerificationContext { get; init; } = connectionString =>
        new PdmDbContext(new DbContextOptionsBuilder<PdmDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options);
    internal TimeSpan VerificationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Только чтение: что изменится при восстановлении копии.</summary>
    public async Task<RestorePlan> PlanRestoreAsync(string path, CancellationToken ct = default)
    {
        var info = backups.Read(path) ?? throw new InvalidDataException("Файл не является резервной копией PdmLite.");
        if (info.Manifest.Format != BackupManifest.CurrentFormat)
            throw new InvalidDataException("Формат резервной копии не поддерживается этой версией PdmLite.");

        var (snapshot, _) = LibraryBackupService.ReadArchive(path);
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
        var before = await backups.CreateCoreAsync(BackupReason.BeforeRestore, progress, ct, recovery.SetBackup).ConfigureAwait(false);
        var (snapshot, _) = LibraryBackupService.ReadArchive(path);
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

    /// <summary>Заменяет все таблицы библиотеки данными копии; счётчики Id — после наибольших.</summary>
    private async Task ReplaceDatabaseAsync(LibrarySnapshot snapshot, RestoreDatabaseAttempt operation, CancellationToken ct)
    {
        await using var db = database.CreateContext();
        operation.ConnectionString = db.Database.GetConnectionString() ?? throw new InvalidOperationException("Нет подключения для проверки фиксации.");
        var actual = new NpgsqlConnectionStringBuilder(operation.ConnectionString);
        var gated = new NpgsqlConnectionStringBuilder(operation.GatedConnectionString);
        if (actual.Host != gated.Host || actual.Port != gated.Port || actual.Database != gated.Database)
            throw new InvalidOperationException("Настройки БД изменились во время восстановления. Повторите после завершения изменения настроек.");
        var tables = new[]
        {
            typeof(LibraryItemReference), typeof(LibraryItemProperty), typeof(LibraryItemVersion), typeof(LibraryItem),
            typeof(LibraryCategoryRule), typeof(LibraryCategory), typeof(DesignationRule), typeof(LibraryLogEntry),
        }.Select(t => Table(db, t)).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [operation.LockKey], ct).ConfigureAwait(false);
        // Имена таблиц — из модели EF (не ввод пользователя) и экранированы как идентификаторы:
        // идентификаторы в SQL нельзя передать параметрами.
        var truncate = "TRUNCATE " + string.Join(", ", tables) + " RESTART IDENTITY";
        await db.Database.ExecuteSqlRawAsync(truncate, ct).ConfigureAwait(false);

        // Сначала разделы-родители, потом вложенные.
        db.LibraryCategories.AddRange(BackupValidation.OrderCategories(snapshot.Categories));
        db.LibraryCategoryRules.AddRange(snapshot.CategoryRules);
        db.LibraryItems.AddRange(snapshot.Items);
        db.Set<LibraryItemVersion>().AddRange(snapshot.Versions);
        db.Set<LibraryItemProperty>().AddRange(snapshot.Properties);
        db.LibraryItemReferences.AddRange(snapshot.References);
        db.DesignationRules.AddRange(snapshot.DesignationRules);
        db.LibraryLog.AddRange(snapshot.Log);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var type in new[] { typeof(LibraryCategory), typeof(LibraryCategoryRule), typeof(LibraryItemVersion), typeof(DesignationRule), typeof(LibraryLogEntry) })
        {
            // Имя таблицы для pg_get_serial_sequence — параметром ({0}); в FROM — экранированный идентификатор.
            var table = Table(db, type);
            var reset = "SELECT setval(pg_get_serial_sequence({0}, 'id'), COALESCE((SELECT MAX(id) FROM " + table + "), 0) + 1, false)";
            await db.Database.ExecuteSqlRawAsync(reset, [table], ct).ConfigureAwait(false);
        }

        db.LibraryLog.Add(new LibraryLogEntry
        {
            At = DateTime.UtcNow,
            User = Environment.UserName,
            Action = "restore",
            Details = operation.Marker,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        operation.CommitStarted = true;
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
    private sealed class RestoreDatabaseAttempt(long lockKey, string marker, string gatedConnectionString)
    {
        public long LockKey { get; } = lockKey;
        public string Marker { get; } = marker;
        public string GatedConnectionString { get; } = gatedConnectionString;
        public bool CommitStarted { get; set; }
        public string? ConnectionString { get; set; }
    }
    internal async Task<(RestoreDatabaseOutcome Outcome, Exception? Error)> VerifyRestoreCommitAsync(long lockKey, string marker, string? connectionString = null)
    {
        // Отмена пользователем не отменяет проверку уже отправленного COMMIT.
        using var timeout = new CancellationTokenSource(VerificationTimeout);
        try
        {
            await using var db = CreateVerificationContext(connectionString ?? database.Settings.BuildConnectionString());
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token).ConfigureAwait(false);
            // Отсутствие маркера проверяется только после завершения исходной транзакции.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [lockKey], timeout.Token).ConfigureAwait(false);
            var committed = await db.LibraryLog.AsNoTracking().AnyAsync(l => l.Action == "restore" && l.Details == marker, timeout.Token).ConfigureAwait(false);
            return (committed ? RestoreDatabaseOutcome.Committed : RestoreDatabaseOutcome.NotCommitted, null);
        }
        catch (Exception ex) { return (RestoreDatabaseOutcome.Unknown, ex); }
    }
    /// <summary>Таблица сущности как идентификатор SQL: «"схема"."таблица"».</summary>
    private static string Table(PdmDbContext db, Type type)
    {
        var entity = db.Model.FindEntityType(type) ?? throw new InvalidOperationException($"Нет таблицы для {type.Name}.");
        return QuoteIdentifier(entity.GetSchema() ?? "public") + "." + QuoteIdentifier(entity.GetTableName()!);
    }
    /// <summary>Идентификатор PostgreSQL в кавычках; кавычки внутри удваиваются.</summary>
    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    /// <summary>
    /// Целостность копии: у каждого компонента есть файл в архиве, и суммы файлов совпадают с записанными
    /// при создании копии. Возвращает описания проблем.
    /// </summary>
    private static async Task<List<string>> VerifyFilesAsync(string path, LibrarySnapshot snapshot, BackupManifest manifest, CancellationToken ct)
    {
        var problems = new List<string>();
        using var zip = ZipFile.OpenRead(path);
        foreach (var item in snapshot.Items.Where(i => zip.GetEntry(LibraryBackupService.FilesPrefix + i.RelativePath) is null))
            problems.Add($"{item.Designation} — нет файла");

        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(LibraryBackupService.FilesPrefix, StringComparison.Ordinal) && e.Name.Length > 0))
            if (!manifest.Checksums.ContainsKey(entry.FullName[LibraryBackupService.FilesPrefix.Length..]))
                problems.Add($"{entry.FullName} — нет контрольной суммы");

        foreach (var (relative, expected) in manifest.Checksums)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(LibraryBackupService.FilesPrefix + relative);
            if (entry is null)
            {
                problems.Add($"{relative} — нет файла");
                continue;
            }
            await using var stream = entry.Open();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{relative} — файл повреждён (контрольная сумма не совпадает)");
        }
        return problems;
    }
    private static void Extract(string path, string folder, LibrarySnapshot snapshot, IProgress<(int, int, string?)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var componentFiles = snapshot.Items.Select(i => i.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(path);
        var files = zip.Entries.Where(e => e.FullName.StartsWith(LibraryBackupService.FilesPrefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var relative = files[i].FullName[LibraryBackupService.FilesPrefix.Length..];
            progress?.Report((i, files.Count, relative));
            var target = ContainedPath.Resolve(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            files[i].ExtractToFile(target, overwrite: true);
            if (componentFiles.Contains(relative))
                LibraryStorage.MakeReadOnly(target);
        }
    }
    private void TryDeleteFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            DeleteDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить папку {Folder}", folder);
        }
    }
}

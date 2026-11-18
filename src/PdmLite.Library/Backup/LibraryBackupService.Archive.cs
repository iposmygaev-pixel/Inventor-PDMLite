using PdmLite.Core.IO;
using Npgsql;
using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Backup;

/// <summary>Внутреннее: снимок базы, замена таблиц, чтение и проверка архива.</summary>
public sealed partial class LibraryBackupService
{
    // ---------- Внутреннее ----------

    private static List<string> LibraryFiles(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList() : [];

    /// <summary>Все таблицы библиотеки на один момент (одна транзакция).</summary>
    private async Task<LibrarySnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        await database.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = database.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        var snapshot = new LibrarySnapshot
        {
            Categories = await db.LibraryCategories.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false),
            CategoryRules = await db.LibraryCategoryRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false),
            Items = await db.LibraryItems.AsNoTracking().OrderBy(i => i.Designation).ToListAsync(ct).ConfigureAwait(false),
            Versions = await db.Set<LibraryItemVersion>().AsNoTracking().OrderBy(v => v.Id).ToListAsync(ct).ConfigureAwait(false),
            Properties = await db.Set<LibraryItemProperty>().AsNoTracking().OrderBy(p => p.ItemId).ThenBy(p => p.Name).ToListAsync(ct).ConfigureAwait(false),
            References = await db.LibraryItemReferences.AsNoTracking().OrderBy(r => r.AssemblyId).ThenBy(r => r.ComponentId).ToListAsync(ct).ConfigureAwait(false),
            DesignationRules = await db.DesignationRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false),
            Log = await db.LibraryLog.AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct).ConfigureAwait(false),
        };
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return snapshot;
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
        foreach (var item in snapshot.Items.Where(i => zip.GetEntry(FilesPrefix + i.RelativePath) is null))
            problems.Add($"{item.Designation} — нет файла");

        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal) && e.Name.Length > 0))
            if (!manifest.Checksums.ContainsKey(entry.FullName[FilesPrefix.Length..]))
                problems.Add($"{entry.FullName} — нет контрольной суммы");

        foreach (var (relative, expected) in manifest.Checksums)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(FilesPrefix + relative);
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

    /// <summary>Копирует поток и считает SHA-256 скопированного.</summary>
    private static async Task<string> CopyWithHashAsync(Stream source, Stream target, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (LibrarySnapshot Snapshot, HashSet<string> Entries) ReadArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var data = zip.GetEntry(DataEntry) ?? throw new InvalidDataException("В копии нет данных базы.");
        using var stream = data.Open();
        var snapshot = JsonSerializer.Deserialize<LibrarySnapshot>(stream, Json) ?? throw new InvalidDataException("Данные базы в копии пусты.");
        BackupValidation.Validate(snapshot, zip.Entries.Select(e => e.FullName));
        return (snapshot, zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal));
    }

    private static void Extract(string path, string folder, LibrarySnapshot snapshot, IProgress<(int, int, string?)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var componentFiles = snapshot.Items.Select(i => i.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(path);
        var files = zip.Entries.Where(e => e.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var relative = files[i].FullName[FilesPrefix.Length..];
            progress?.Report((i, files.Count, relative));
            var target = ContainedPath.Resolve(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            files[i].ExtractToFile(target, overwrite: true);
            if (componentFiles.Contains(relative))
                LibraryStorage.MakeReadOnly(target);
        }
    }

    private static async Task WriteEntryAsync<T>(ZipArchive zip, string name, T value, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, Json, ct).ConfigureAwait(false);
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить {Path}", path);
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

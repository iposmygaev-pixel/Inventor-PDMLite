using System.Security.Cryptography;
using PdmLite.Library.Items;
using System.Data;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Backup;

/// <summary>Зачем сделана копия (часть имени файла).</summary>
public enum BackupReason
{
    Manual,
    Scheduled,
    BeforeMaintenance,
    BeforeMerge,
    BeforeRestore,
}

/// <summary>Описание копии — <c>manifest.json</c> в архиве.</summary>
public sealed record BackupManifest
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    public DateTime CreatedAt { get; init; }
    public string User { get; init; } = "";
    public string Machine { get; init; } = "";
    public BackupReason Reason { get; init; }
    public int Items { get; init; }
    public int Files { get; init; }
    public long FilesSize { get; init; }

    /// <summary>Папка библиотеки на момент копии (для справки: восстанавливается в текущую).</summary>
    public string LibraryRoot { get; init; } = "";

    /// <summary>Контрольные суммы (SHA-256) файлов архива: путь в библиотеке → сумма. Для проверки целостности копии.</summary>
    public Dictionary<string, string> Checksums { get; init; } = [];
}

/// <summary>Копия в папке резервных копий.</summary>
public sealed record BackupInfo(string Path, BackupManifest Manifest, long Size)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsAutomatic => Manifest.Reason != BackupReason.Manual;
}

/// <summary>Что изменится при восстановлении (по сравнению с текущей библиотекой).</summary>
/// <param name="Added">Компоненты, которых сейчас нет — появятся.</param>
/// <param name="Removed">Компоненты, которых нет в копии — пропадут.</param>
/// <param name="Changed">Компоненты, у которых изменились обозначение, путь или файл — вернутся к состоянию копии.</param>
/// <param name="MissingFiles">Компоненты копии без файла в архиве или с изменённым файлом (контрольная сумма не совпала) — копия повреждена.</param>
public sealed record RestorePlan(BackupInfo Backup, IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
    IReadOnlyList<string> Changed, int ItemsNow, int FilesNow, IReadOnlyList<string> MissingFiles)
{
    public bool IsDamaged => MissingFiles.Count > 0;
}

/// <summary>Где хранить копии и сколько.</summary>
public sealed record BackupOptions(string Folder, int Keep, int IntervalDays);

/// <summary>
/// Резервные копии библиотеки: база (все таблицы схемы library) и файлы папки библиотеки — в одном
/// ZIP-архиве. Восстановление заменяет базу и папку целиком; перед ним делается копия текущего состояния,
/// при ошибке выполняется откат; неполный откат сообщается явно.
/// </summary>
public sealed class LibraryBackupService(
    IPdmDatabase database,
    Func<LibraryStorage> storageFactory,
    Func<BackupOptions> options,
    ILogger<LibraryBackupService> logger)
{
    private const string ManifestEntry = "manifest.json";
    private const string DataEntry = "data/library.json";
    internal const string FilesPrefix = "files/";

    // Для LibraryRestoreService: восстановление идёт в ту же базу и папку, копию которых делает этот сервис.
    internal IPdmDatabase Database => database;
    internal Func<LibraryStorage> StorageFactory => storageFactory;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ReasonText(BackupReason reason) => reason switch
    {
        BackupReason.Manual => "вручную",
        BackupReason.Scheduled => "по расписанию",
        BackupReason.BeforeMaintenance => "перед обслуживанием",
        BackupReason.BeforeMerge => "перед объединением",
        BackupReason.BeforeRestore => "перед восстановлением",
        _ => reason.ToString(),
    };

    // ---------- Создание ----------

    /// <summary>Делает копию: база и все файлы библиотеки. Затем удаляет старые автоматические копии сверх лимита.</summary>
    public Task<BackupInfo> CreateAsync(BackupReason reason, IProgress<(int Done, int Total, string? Current)>? progress = null,
        CancellationToken ct = default) => CreateCoreAsync(reason, progress, ct, null);

    internal async Task<BackupInfo> CreateCoreAsync(BackupReason reason, IProgress<(int Done, int Total, string? Current)>? progress,
        CancellationToken ct, Action<string>? reserve)
    {
        var opts = options();
        Directory.CreateDirectory(opts.Folder);
        var storage = storageFactory();

        var backupFolder = Path.GetFullPath(opts.Folder);
        if (backupFolder.Equals(storage.Root, StringComparison.OrdinalIgnoreCase) || storage.Contains(backupFolder))
            throw new InvalidOperationException("Папка резервных копий должна находиться вне библиотеки.");
        var files = LibraryFiles(storage.Root);
        // Файлы должны оставаться неизменными до и после снимка БД и всего копирования.
        var baseline = new Dictionary<string, (string Hash, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) baseline[file] = await LibraryStorage.HashAsync(file, ct).ConfigureAwait(false);
        var snapshot = await LoadSnapshotAsync(ct).ConfigureAwait(false);

        var now = DateTime.Now;
        var name = $"PdmLite-Library_{now:yyyy-MM-dd_HH-mm-ss}_{ReasonText(reason).Replace(' ', '-')}_{Guid.NewGuid():N}";
        var path = Path.Combine(opts.Folder, name + ".zip");
        reserve?.Invoke(path); // Защита safety-копии действует до её появления и до первого Prune.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        long size = 0;
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                for (var i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(storage.Root, files[i]).Replace(Path.DirectorySeparatorChar, '/');
                    progress?.Report((i, files.Count, relative));
                    var entry = zip.CreateEntry(FilesPrefix + relative, CompressionLevel.Fastest);
                    entry.LastWriteTime = File.GetLastWriteTime(files[i]);
                    // Файл может быть открыт в Inventor — читаем, не мешая ему.
                    await using var source = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await using var target = entry.Open();
                    checksums[relative] = await CopyWithHashAsync(source, target, ct).ConfigureAwait(false);
                    if (checksums[relative] != baseline[files[i]].Hash || source.Length != baseline[files[i]].Size)
                        throw new IOException("Файлы библиотеки изменились во время резервного копирования. Повторите создание копии.");
                    size += source.Length;
                }

                if (!files.SequenceEqual(LibraryFiles(storage.Root), StringComparer.OrdinalIgnoreCase))
                    throw new IOException("Состав файлов библиотеки изменился во время резервного копирования.");
                foreach (var file in files)
                    if ((await LibraryStorage.HashAsync(file, ct).ConfigureAwait(false)) != baseline[file])
                        throw new IOException("Файлы библиотеки изменились после копирования. Повторите создание копии.");
                var current = await LoadSnapshotAsync(ct).ConfigureAwait(false);
                if (JsonSerializer.Serialize(snapshot, Json) != JsonSerializer.Serialize(current, Json))
                    throw new IOException("База библиотеки изменилась во время резервного копирования. Повторите создание копии.");
                await WriteEntryAsync(zip, DataEntry, snapshot, ct).ConfigureAwait(false);
                var manifest = new BackupManifest
                {
                    CreatedAt = DateTime.UtcNow,
                    User = Environment.UserName,
                    Machine = Environment.MachineName,
                    Reason = reason,
                    Items = snapshot.Items.Count,
                    Files = files.Count,
                    FilesSize = size,
                    LibraryRoot = storage.Root,
                    Checksums = checksums,
                };
                await WriteEntryAsync(zip, ManifestEntry, manifest, ct).ConfigureAwait(false);
            }

            File.Move(temp, path);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }

        progress?.Report((files.Count, files.Count, null));
        logger.LogInformation("Резервная копия библиотеки: {Path} ({Items} компонентов, {Files} файлов)", path, snapshot.Items.Count, files.Count);
        Prune();
        return Read(path) ?? throw new InvalidDataException("Созданная копия не читается.");
    }

    /// <summary>Пора ли делать копию по расписанию (последняя копия любого вида старше интервала).</summary>
    public bool IsScheduledDue()
    {
        var opts = options();
        if (opts.IntervalDays <= 0)
            return false;
        var last = List().FirstOrDefault();
        return last is null || DateTime.UtcNow - last.Manifest.CreatedAt >= TimeSpan.FromDays(opts.IntervalDays);
    }

    // ---------- Список ----------

    /// <summary>Копии в папке, новые первыми. Нечитаемые архивы пропускаются.</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        var folder = options().Folder;
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder, "*.zip")
            .Select(Read)
            .OfType<BackupInfo>()
            .OrderByDescending(b => b.Manifest.CreatedAt)
            .ToList();
    }

    /// <summary>Удаляет автоматические копии сверх лимита (ручные не трогает). Возвращает число удалённых.</summary>
    public int Prune()
    {
        // Защищаем копию текущего состояния, а не исходный архив: тот уже закреплён частной копией.
        string? protectedBackup = null;
        var recoveryPath = Path.TrimEndingDirectorySeparator(storageFactory().Root) + ".restore-recovery.json";
        if (File.Exists(recoveryPath))
        {
            try
            {
                using var file = new FileStream(recoveryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var journal = JsonDocument.Parse(file);
                protectedBackup = journal.RootElement.GetProperty("backup").GetString();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Не удалось прочитать журнал восстановления; очистка копий пропущена");
                return 0;
            }
        }
        var keep = Math.Max(1, options().Keep);
        var extra = List().Where(b => b.IsAutomatic).Skip(keep)
            .Where(b => !string.Equals(b.Path, protectedBackup, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var backup in extra)
            TryDeleteFile(backup.Path);
        return extra.Count;
    }

    public void Delete(BackupInfo backup) => File.Delete(backup.Path);

    /// <summary>Описание копии или <c>null</c>, если файл — не копия PdmLite.</summary>
    public BackupInfo? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestEntry);
            if (entry is null)
                return null;
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, Json);
            return manifest is null || manifest.Checksums is null || manifest.Items < 0 || manifest.Files < 0 || manifest.FilesSize < 0
                ? null : new BackupInfo(path, manifest, new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не читается резервная копия {Path}", path);
            return null;
        }
    }

    // ---------- Внутреннее: снимок базы и архив ----------

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

    internal static (LibrarySnapshot Snapshot, HashSet<string> Entries) ReadArchive(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var data = zip.GetEntry(DataEntry) ?? throw new InvalidDataException("В копии нет данных базы.");
        using var stream = data.Open();
        var snapshot = JsonSerializer.Deserialize<LibrarySnapshot>(stream, Json) ?? throw new InvalidDataException("Данные базы в копии пусты.");
        BackupValidation.Validate(snapshot, zip.Entries.Select(e => e.FullName));
        return (snapshot, zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal));
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
}

/// <summary>Все таблицы библиотеки — содержимое <c>data/library.json</c>.</summary>
public sealed class LibrarySnapshot
{
    public List<LibraryCategory> Categories { get; init; } = [];
    public List<LibraryCategoryRule> CategoryRules { get; init; } = [];
    public List<LibraryItem> Items { get; init; } = [];
    public List<LibraryItemVersion> Versions { get; init; } = [];
    public List<LibraryItemProperty> Properties { get; init; } = [];
    public List<LibraryItemReference> References { get; init; } = [];
    public List<DesignationRule> DesignationRules { get; init; } = [];
    public List<LibraryLogEntry> Log { get; init; } = [];
}

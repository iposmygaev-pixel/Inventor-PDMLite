using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Checks;
using PdmLite.Library.Import;
using PdmLite.Library.Rules;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Резервные копии — испорченные и опасные архивы, расписание.</summary>
public class BackupEdgeTests
{
    private static (LibraryImportService Import, LibraryBackupService Backup, string Folder) Create(TestDatabase db, TempFolders folders, int keep = 10, int interval = 7)
    {
        LibraryStorage Storage() => new(folders.Library);
        var backups = Path.Combine(folders.Root, "Backups");
        return (new LibraryImportService(db.Database, new FakePropertyStore(), Storage, NullLogger<LibraryImportService>.Instance),
            new LibraryBackupService(db.Database, Storage, () => new BackupOptions(backups, keep, interval), NullLogger<LibraryBackupService>.Instance),
            backups);
    }

    [Theory]
    [InlineData(BackupReason.Manual)]
    [InlineData(BackupReason.Scheduled)]
    [InlineData(BackupReason.BeforeMaintenance)]
    [InlineData(BackupReason.BeforeMerge)]
    [InlineData(BackupReason.BeforeRestore)]
    public void ReasonText_ForEveryReason(BackupReason reason) =>
        Assert.False(string.IsNullOrWhiteSpace(LibraryBackupService.ReasonText(reason)));

    [Fact]
    public void List_NoFolder_Empty_IntervalZero_NeverDue()
    {
        using var folders = new TempFolders();
        var service = new LibraryBackupService(null!, () => new LibraryStorage(folders.Library),
            () => new BackupOptions(Path.Combine(folders.Root, "нет"), 3, 0), NullLogger<LibraryBackupService>.Instance);
        Assert.Empty(service.List());
        Assert.False(service.IsScheduledDue());
    }

    [Fact]
    public void List_SkipsJunkZipAndNonZip()
    {
        using var folders = new TempFolders();
        var backups = Path.Combine(folders.Root, "Backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "мусор.zip"), "не архив");
        using (var zip = ZipFile.Open(Path.Combine(backups, "чужой.zip"), ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        using (var zip = ZipFile.Open(Path.Combine(backups, "кривой.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write("{не json");
        }
        var service = new LibraryBackupService(null!, () => new LibraryStorage(folders.Library),
            () => new BackupOptions(backups, 3, 7), NullLogger<LibraryBackupService>.Instance);

        Assert.Empty(service.List());
        Assert.True(service.IsScheduledDue()); // копий нет — пора
        Assert.Null(service.Read(Path.Combine(backups, "нет.zip")));
    }

    [PostgresFact]
    public async Task Restore_ZipSlipEntry_Rejected_LibraryUntouched_NoStagingLeft()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (import, backup, _) = Create(db, folders);
        var file = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt");
        await import.CommitAsync(await import.AnalyzeAsync([file]));
        var created = await backup.CreateAsync(BackupReason.Manual);
        using (var zip = ZipFile.Open(created.Path, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("files/../../evil.txt").Open());
            writer.Write("зло");
        }
        var before = Directory.EnumerateFiles(folders.Library, "*", SearchOption.AllDirectories).Order().ToList();

        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreAsync(created.Path));

        Assert.Equal(before, Directory.EnumerateFiles(folders.Library, "*", SearchOption.AllDirectories).Order().ToList());
        Assert.False(File.Exists(Path.Combine(folders.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folders.Root)!, "evil.txt")));
        Assert.Empty(Directory.EnumerateDirectories(folders.Root, "Library.restore-*"));
    }

    [PostgresFact]
    public async Task Plan_ChangedFile_ListedAsDamaged_RestoreRefused()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (import, backup, _) = Create(db, folders);
        await import.CommitAsync(await import.AnalyzeAsync([folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "bolt")]));
        var created = await backup.CreateAsync(BackupReason.Manual);
        using (var zip = ZipFile.Open(created.Path, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.Single(e => e.FullName.StartsWith("files/") && e.Name.Length > 0);
            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("подменён");
        }

        var plan = await backup.PlanRestoreAsync(created.Path);

        Assert.True(plan.IsDamaged);
        Assert.Contains(plan.MissingFiles, m => m.Contains("контрольная сумма"));
        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreAsync(created.Path));
    }

    [PostgresFact]
    public async Task Prune_KeepZeroMeansOne()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (_, backup, _) = Create(db, folders, keep: 0);
        await backup.CreateAsync(BackupReason.Scheduled);
        await Task.Delay(1100); // имя копии — с точностью до секунды
        await backup.CreateAsync(BackupReason.Scheduled);
        Assert.Single(backup.List());
        Assert.False(backup.IsScheduledDue());
    }
}

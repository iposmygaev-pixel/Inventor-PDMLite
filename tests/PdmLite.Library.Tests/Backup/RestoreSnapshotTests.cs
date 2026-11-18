using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Index.Operations;
using PdmLite.Library.Backup;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class RestoreSnapshotTests : IDisposable
{
    private readonly TempFolders _folders = new();

    private sealed record Models(Guid Keep, Guid Remove, Guid Assembly, int Category, string KeepPath, string RemovePath, string AssemblyPath);
    private LibraryBackupService Backups(IPdmDatabase db) => new(db, () => new LibraryStorage(_folders.Library),
        () => new BackupOptions(Path.Combine(_folders.Root, "Backups"), 10, 7), NullLogger<LibraryBackupService>.Instance);

    private async Task<Models> SeedAsync(IPdmDatabase database)
    {
        await database.MigrateAsync();
        var keepPath = _folders.InLibrary("keep/keep.ipt"); var removePath = _folders.InLibrary("remove/remove.ipt"); var assemblyPath = _folders.InLibrary("assemblies/assembly.iam");
        FileBackedStore.Create(keepPath, "Keep", "Keep"); FileBackedStore.Create(removePath, "Remove", "Remove");
        FileBackedStore.Create(assemblyPath, "Assembly", "Assembly", removePath);
        await using var db = database.CreateContext();
        var category = new LibraryCategory { Name = "Review" }; db.LibraryCategories.Add(category); await db.SaveChangesAsync();
        async Task<LibraryItem> Model(string path, string name, LibraryDocumentType type)
        {
            var (hash, size) = await LibraryStorage.HashAsync(path);
            var relative = Path.GetRelativePath(_folders.Library, path).Replace('\\', '/');
            return new() { Id = Guid.NewGuid(), CategoryId = category.Id, Kind = LibraryItemKind.Purchased, DocumentType = type,
                Designation = name, DesignationKey = name.ToUpperInvariant(), Manufacturer = "Test", RelativePath = relative,
                FileHash = hash, FileSize = size, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, CreatedBy = "test", ModifiedBy = "test",
                Versions = [new() { Version = 1, RelativePath = relative, FileHash = hash, FileSize = size, CreatedAt = DateTime.UtcNow, CreatedBy = "test" }] };
        }
        var keep = await Model(keepPath, "Keep", LibraryDocumentType.Part); var remove = await Model(removePath, "Remove", LibraryDocumentType.Part);
        var assembly = await Model(assemblyPath, "Assembly", LibraryDocumentType.Assembly);
        assembly.References.Add(new() { AssemblyId = assembly.Id, ComponentId = remove.Id });
        db.LibraryItems.AddRange(keep, remove, assembly); await db.SaveChangesAsync();
        foreach (var path in new[] { keepPath, removePath, assemblyPath }) LibraryStorage.MakeReadOnly(path);
        return new(keep.Id, remove.Id, assembly.Id, category.Id, keepPath, removePath, assemblyPath);
    }

    private sealed class CallbackProgress(Action<(int Done, int Total, string? Current)> report) : IProgress<(int Done, int Total, string? Current)>
    { public void Report((int Done, int Total, string? Current) value) => report(value); }
    public void Dispose() => _folders.Dispose();

    [PostgresFact]
    public async Task Restore_SourceArchiveChangedDuringSafetyBackupUsesValidatedSnapshot() => await RestoreWithSourceChange(false);

    [PostgresFact]
    public async Task Restore_SourceArchiveDeletedDuringSafetyBackupUsesValidatedSnapshot() => await RestoreWithSourceChange(true);

    private async Task RestoreWithSourceChange(bool delete)
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var service = Backups(db.Database); var expected = File.ReadAllText(model.KeepPath);
        var source = await service.CreateAsync(BackupReason.Manual);
        LibraryStorage.ClearReadOnly(model.KeepPath); File.AppendAllText(model.KeepPath, "current changes");
        var changed = false;
        var progress = new CallbackProgress(p =>
        {
            if (changed || p.Done != 0 || p.Current is null) return; changed = true;
            if (delete) File.Delete(source.Path);
            else
            {
                using var zip = ZipFile.Open(source.Path, ZipArchiveMode.Update);
                var name = "files/" + Path.GetRelativePath(_folders.Library, model.KeepPath).Replace('\\', '/');
                zip.GetEntry(name)!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("tampered after validation");
            }
        });
        await service.RestoreAsync(source.Path, progress);
        Assert.True(changed); Assert.Equal(expected, File.ReadAllText(model.KeepPath));
        if (delete) Assert.False(File.Exists(source.Path));
        else Assert.True((await service.PlanRestoreAsync(source.Path)).IsDamaged);
    }

    [PostgresFact]
    public async Task Restore_PruneMayRemoveOriginalAutomaticArchiveButNotRestoreSnapshot()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var service = new LibraryBackupService(db.Database, () => new LibraryStorage(_folders.Library),
            () => new BackupOptions(Path.Combine(_folders.Root, "Backups"), 1, 7), NullLogger<LibraryBackupService>.Instance);
        var expected = File.ReadAllText(model.KeepPath); var source = await service.CreateAsync(BackupReason.Scheduled);
        LibraryStorage.ClearReadOnly(model.KeepPath); File.AppendAllText(model.KeepPath, "current changes");
        var safety = await service.RestoreAsync(source.Path);
        Assert.False(File.Exists(source.Path)); Assert.True(File.Exists(safety.Path));
        Assert.Equal(expected, File.ReadAllText(model.KeepPath));
    }
}

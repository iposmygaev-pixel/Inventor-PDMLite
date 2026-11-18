using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class BackupConsistencyTests : IDisposable
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

    [PostgresFact]
    public async Task Backup_ConcurrentDatabaseAndFileChangeIsRejectedBeforeArchivePublication()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database); var changed = false;
        var progress = new CallbackProgress(p =>
        {
            if (changed || p.Done != 0 || p.Current is null) return; changed = true;
            LibraryStorage.ClearReadOnly(model.KeepPath); File.AppendAllText(model.KeepPath, "new revision");
            using var context = db.Database.CreateContext(); var item = context.LibraryItems.Single(i => i.Id == model.Keep);
            item.Designation = "Changed during backup"; context.SaveChanges();
        });
        await Assert.ThrowsAnyAsync<IOException>(() => Backups(db.Database).CreateAsync(BackupReason.Manual, progress));
        Assert.True(changed); Assert.Empty(Backups(db.Database).List());
        Assert.Empty(Directory.GetFiles(Path.Combine(_folders.Root, "Backups"), "*.part"));
    }

    [PostgresFact]
    public async Task Backup_ChangeToAlreadyCopiedFileIsRejected()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database); var changed = false;
        var progress = new CallbackProgress(p =>
        {
            if (changed || p.Done != 1 || p.Current is null) return; changed = true;
            LibraryStorage.ClearReadOnly(model.AssemblyPath); File.AppendAllText(model.AssemblyPath, "changed after copy");
        });
        await Assert.ThrowsAnyAsync<IOException>(() => Backups(db.Database).CreateAsync(BackupReason.Manual, progress));
        Assert.True(changed); Assert.Empty(Backups(db.Database).List());
    }

    [PostgresFact]
    public async Task Backup_DatabaseOnlyChangeIsDetectedAndPreviousBackupIsKept()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var service = Backups(db.Database); var previous = await service.CreateAsync(BackupReason.Manual); var changed = false;
        var progress = new CallbackProgress(p =>
        {
            if (changed || p.Done != 0 || p.Current is null) return; changed = true;
            using var context = db.Database.CreateContext(); var item = context.LibraryItems.Single(i => i.Id == model.Keep);
            item.Designation = "Changed metadata during backup"; context.SaveChanges();
        });
        await Assert.ThrowsAnyAsync<IOException>(() => service.CreateAsync(BackupReason.Manual, progress));
        Assert.True(changed); Assert.Equal(previous.Path, Assert.Single(service.List()).Path);
        Assert.False((await service.Restorer().PlanRestoreAsync(previous.Path)).IsDamaged);
    }

    private sealed class CallbackProgress(Action<(int Done, int Total, string? Current)> report) : IProgress<(int Done, int Total, string? Current)>
    { public void Report((int Done, int Total, string? Current) value) => report(value); }
    public void Dispose() => _folders.Dispose();
}

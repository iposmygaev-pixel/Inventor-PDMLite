using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Data.Library;
using PdmLite.Library.Backup;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class ItemRollbackTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly SwitchableStore _store = new();

    private sealed record Models(Guid Keep, Guid Remove, Guid Assembly, int Category, string KeepPath, string RemovePath, string AssemblyPath);
    private LibraryItemService Items(IPdmDatabase db) => new(db, _store, () => new LibraryStorage(_folders.Library), NullLogger<LibraryItemService>.Instance);

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
    public async Task Edit_SilentReferenceReplacementFailureRollsBackFilesAndDatabase()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var before = File.ReadAllText(model.AssemblyPath); _store.Inner.IgnoreReplaceFor = "assembly.iam";
        var service = Items(db.Database); var catalog = await service.LoadCatalogAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.ApplyEditAsync(catalog, model.Remove,
            new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category)));
        Assert.True(File.Exists(model.RemovePath)); Assert.Equal(before, File.ReadAllText(model.AssemblyPath));
        await using var after = db.Database.CreateContext(); Assert.Equal("Remove", (await after.LibraryItems.FindAsync(model.Remove))!.Designation);
        Assert.False(await after.LibraryLog.AnyAsync(l => l.Action == "edit"));
    }

    [PostgresFact]
    public async Task Merge_SilentReferenceReplacementFailureDoesNotDeleteComponent()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        _store.Inner.IgnoreReplaceFor = "assembly.iam";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Items(db.Database).Duplicates().MergeDuplicatesAsync(model.Keep, [model.Remove]));
        Assert.True(File.Exists(model.RemovePath)); Assert.Equal([model.RemovePath], FileBackedStore.References(model.AssemblyPath));
        await using var after = db.Database.CreateContext(); Assert.NotNull(await after.LibraryItems.FindAsync(model.Remove));
    }

    [PostgresFact]
    public async Task Relink_UpdatesAssemblyCurrentVersionHashAndSize()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database); var service = Items(db.Database);
        await service.ApplyEditAsync(await service.LoadCatalogAsync(), model.Remove,
            new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category));
        await using var after = db.Database.CreateContext(); var assembly = await after.LibraryItems.Include(i => i.Versions).SingleAsync(i => i.Id == model.Assembly);
        var current = Assert.Single(assembly.Versions, v => v.Version == assembly.Version);
        var (hash, size) = await LibraryStorage.HashAsync(model.AssemblyPath);
        Assert.Equal(hash, assembly.FileHash); Assert.Equal(hash, current.FileHash); Assert.Equal(size, current.FileSize);
    }

    [PostgresFact]
    public async Task Merge_FileCannotBeRemoved_DoesNotCommitPartialSuccess()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var folder = Path.GetDirectoryName(model.RemovePath)!; FileStream? locked = null; UnixFileMode mode = default;
        try
        {
            if (OperatingSystem.IsWindows()) locked = File.Open(model.RemovePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            else { mode = File.GetUnixFileMode(folder); File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute); }
            await Assert.ThrowsAnyAsync<Exception>(() => Items(db.Database).Duplicates().MergeDuplicatesAsync(model.Keep, [model.Remove]));
        }
        finally { locked?.Dispose(); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, mode); }
        await using var after = db.Database.CreateContext(); Assert.NotNull(await after.LibraryItems.FindAsync(model.Remove));
        Assert.Equal([model.RemovePath], FileBackedStore.References(model.AssemblyPath));
        Assert.False(await after.LibraryLog.AnyAsync(l => l.Action == "merge"));
    }
    private sealed class SwitchableStore : IDocumentPropertyStore
    {
        public FileBackedStore Inner { get; } = new();
        public Action<string>? BeforeWrite { get; set; }
        public Action<string>? BeforeReplace { get; set; }
        public HashSet<string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) => ReadWithoutGeometryAsync(path, cancellationToken);
        public Task<DocumentProperties> ReadWithoutGeometryAsync(string path, CancellationToken cancellationToken = default) =>
            Failed.Contains(path) ? throw new IOException("Unreadable parent") : Inner.ReadWithoutGeometryAsync(path, cancellationToken);
        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default)
        { BeforeWrite?.Invoke(path); return Inner.WriteAsync(path, changes, cancellationToken); }
        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken cancellationToken = default)
        { BeforeReplace?.Invoke(path); return Inner.ReplaceReferencesAsync(path, map, cancellationToken); }
    }
    public void Dispose() => _folders.Dispose();

    [PostgresFact]
    public async Task Edit_DestinationAppearsAfterValidation_FailedMovePreservesBothFiles()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var items = Items(db.Database); var catalog = await items.LoadCatalogAsync();
        var edit = new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category);
        var check = await items.CheckEditAsync(catalog, model.Remove, edit);
        Assert.False(check.HasErrors);
        var destination = _folders.InLibrary(check.NewRelativePath!);
        var original = File.ReadAllText(model.RemovePath);
        var collision = new DestinationCollision(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "created by another operation");
        });
        var racing = new LibraryItemService(new CollisionDatabase(db.Database, collision), _store,
            () => new LibraryStorage(_folders.Library), NullLogger<LibraryItemService>.Instance);

        await Assert.ThrowsAsync<IOException>(() => racing.ApplyEditAsync(catalog, model.Remove, edit));

        Assert.True(collision.Fired);
        Assert.Equal(original, File.ReadAllText(model.RemovePath));
        Assert.Equal("created by another operation", File.ReadAllText(destination));
        Assert.Equal([model.RemovePath], FileBackedStore.References(model.AssemblyPath));
        await using var after = db.Database.CreateContext();
        Assert.Equal("Remove", (await after.LibraryItems.FindAsync(model.Remove))!.Designation);
        Assert.False(await after.LibraryLog.AnyAsync(l => l.Action == "edit"));
    }

    [PostgresFact]
    public async Task Edit_FailedRollbackKeepsCopiesAndReportsIncompleteRecovery()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var original = File.ReadAllText(model.RemovePath);
        _store.BeforeWrite = _ => { Directory.CreateDirectory(model.RemovePath); throw new IOException("Original write failure"); };
        var items = Items(db.Database); var catalog = await items.LoadCatalogAsync(); LibraryRollbackException? failure = null;
        try
        {
            failure = await Assert.ThrowsAsync<LibraryRollbackException>(() => items.ApplyEditAsync(
                catalog, model.Remove,
                new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category)));
            Assert.Contains("восстановление неполное", failure.Message);
            var copy = Assert.Single(failure.Copies, c => c.Original == model.RemovePath);
            Assert.True(File.Exists(copy.Backup)); Assert.Equal(original, File.ReadAllText(copy.Backup));
            Assert.Contains("Original write failure", failure.InnerException!.ToString());
            Assert.Contains(copy.Backup, failure.Message);
            await using var after = db.Database.CreateContext();
            Assert.Equal("Remove", (await after.LibraryItems.FindAsync(model.Remove))!.Designation);
        }
        finally { if (failure is not null) foreach (var copy in failure.Copies) LibraryStorage.Delete(copy.Backup); }
    }

    [PostgresFact]
    public async Task Merge_FailedAssemblyRollbackKeepsCopiesAndDoesNotHideOriginalFailure()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        var original = File.ReadAllText(model.AssemblyPath);
        _store.BeforeReplace = path =>
        { File.Delete(path); Directory.CreateDirectory(path); throw new IOException("Original relink failure"); };
        LibraryRollbackException? failure = null;
        try
        {
            failure = await Assert.ThrowsAsync<LibraryRollbackException>(() => Items(db.Database).Duplicates().MergeDuplicatesAsync(model.Keep, [model.Remove]));
            var copy = Assert.Single(failure.Copies, c => c.Original == model.AssemblyPath);
            Assert.Equal(original, File.ReadAllText(copy.Backup)); Assert.Contains(copy.Backup, failure.Message);
            Assert.Contains("Original relink failure", failure.InnerException!.ToString());
            await using var after = db.Database.CreateContext(); Assert.NotNull(await after.LibraryItems.FindAsync(model.Remove));
        }
        finally { if (failure is not null) foreach (var copy in failure.Copies) LibraryStorage.Delete(copy.Backup); }
    }

    [PostgresFact]
    public async Task Edit_CleanupFailureAfterCommitIsNotReportedAsFailedSave()
    {
        await using var db = await TestDatabase.CreateAsync(); var model = await SeedAsync(db.Database);
        DirectoryBlock? blocked = null; var oldFolder = Path.GetDirectoryName(model.RemovePath)!;
        _store.BeforeWrite = _ => blocked = new DirectoryBlock(oldFolder);
        try
        {
            var items = Items(db.Database); var catalog = await items.LoadCatalogAsync();
            await items.ApplyEditAsync(catalog, model.Remove, new LibraryItemEdit("Renamed", LibraryItemKind.Purchased, "Test", model.Category));
            Assert.True(Directory.Exists(oldFolder));
            await using var after = db.Database.CreateContext(); var item = (await after.LibraryItems.FindAsync(model.Remove))!;
            Assert.Equal("Renamed", item.Designation); Assert.True(File.Exists(_folders.InLibrary(item.RelativePath)));
            Assert.True(await after.LibraryLog.AnyAsync(l => l.Action == "edit"));
        }
        finally { blocked?.Dispose(); }
    }

    // Инъекция на фактическом чтении редактируемой карточки после CheckEdit, а не по счётчику factory-вызовов.
    private sealed class DestinationCollision(Action create) : DbCommandInterceptor
    {
        public bool Fired;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!Fired && command.CommandText.Contains("item_versions")) { Fired = true; create(); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CollisionDatabase(IPdmDatabase inner, DestinationCollision collision) : IPdmDatabase
    {
        public bool IsConfigured => inner.IsConfigured;
        public DatabaseSettings Settings => inner.Settings;
        public void Configure(DatabaseSettings value) => inner.Configure(value);
        public PdmDbContext CreateContext() => new(new DbContextOptionsBuilder<PdmDbContext>().UseNpgsql(Settings.BuildConnectionString())
            .UseSnakeCaseNamingConvention().AddInterceptors(collision).Options);
        public Task MigrateAsync(CancellationToken ct = default) => inner.MigrateAsync(ct);
        public Task<(bool Ok, string Message)> TestAsync(DatabaseSettings value, CancellationToken ct = default) => inner.TestAsync(value, ct);
    }

    private sealed class DirectoryBlock : IDisposable
    {
        private readonly string _path;
        private readonly UnixFileMode _mode;
        private readonly SafeFileHandle? _handle;
        public DirectoryBlock(string path)
        {
            _path = path;
            if (OperatingSystem.IsWindows())
            {
                _handle = CreateFile(path, 1, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (_handle.IsInvalid) throw new IOException("Cannot lock test directory", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            else { _mode = File.GetUnixFileMode(path); File.SetUnixFileMode(path, 0); }
        }
        public void Dispose()
        {
            _handle?.Dispose();
            if (!OperatingSystem.IsWindows() && Directory.Exists(_path)) File.SetUnixFileMode(_path, _mode);
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
}

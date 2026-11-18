using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Index;
using PdmLite.Index.Operations;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class RenamePlanUnreadableParentTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly SwitchableStore _store = new();
    private string Project => Path.Combine(_folders.Root, "Project");
    private string AddProject(string name, params string[] refs)
    {
        var path = Path.Combine(Project, name); FileBackedStore.Create(path, name, "Test", refs); return path;
    }
    private ReferenceIndexService Index(IPdmDatabase db) => new(db, _store, NullLogger<ReferenceIndexService>.Instance);
    private ProjectFileOperations Operations(IPdmDatabase db) => new(db, _store, Index(db), new NoOpenDocuments(), NullLogger<ProjectFileOperations>.Instance);

    [Fact]
    public async Task RenamePlan_WithoutDatabase_UnreadableParentBlocksOperation()
    {
        var part = AddProject("part.ipt"); var parent = AddProject("assembly.iam", part); _store.Failed.Add(parent);
        var db = new PdmDatabase(new DatabaseSettings(), NullLoggerFactory.Instance);
        var operations = Operations(db);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => operations.PlanAsync(Project, [new(part, Path.Combine(Project, "new.ipt"))], []));
        Assert.True(File.Exists(part)); Assert.Equal([part], FileBackedStore.References(parent));
        _store.Failed.Clear();
        var plan = await operations.PlanAsync(Project, [new(part, Path.Combine(Project, "new.ipt"))], []);
        Assert.True(plan.CanExecute); Assert.Contains(plan.References, r => r.Document == parent);
    }

    [PostgresFact]
    public async Task RenamePlan_WithDatabase_UnreadableParentBlocksOperation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var part = AddProject("part.ipt"); var parent = AddProject("assembly.iam", part); _store.Failed.Add(parent);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Operations(db.Database).PlanAsync(Project, [new(part, Path.Combine(Project, "new.ipt"))], []));
        Assert.True(File.Exists(part)); Assert.Equal([part], FileBackedStore.References(parent));
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
}

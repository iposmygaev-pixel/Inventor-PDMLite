using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Index;
using PdmLite.Library.Backup;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class ReferenceIndexRescanTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly SwitchableStore _store = new();
    private string Project => Path.Combine(_folders.Root, "Project");
    private string AddProject(string name, params string[] refs)
    {
        var path = Path.Combine(Project, name); FileBackedStore.Create(path, name, "Test", refs); return path;
    }
    private ReferenceIndexService Index(IPdmDatabase db) => new(db, _store, NullLogger<ReferenceIndexService>.Instance);

    [PostgresFact]
    public async Task FailedRescan_KeepsLastKnownReferences_AndDoesNotClaimVerifiedTimestamp()
    {
        await using var db = await TestDatabase.CreateAsync(); var part = AddProject("part.ipt"); var parent = AddProject("assembly.iam", part);
        var index = Index(db.Database); await index.ScanAsync(Project);
        File.AppendAllText(parent, "changed"); _store.Failed.Add(parent);
        Assert.Single((await index.ScanAsync(Project)).Errors);
        Assert.Contains(await index.ComponentsAsync(parent), d => d.Path == part);
        Assert.Null(await index.ScannedAtAsync(parent));
    }

    [PostgresFact]
    public async Task FailedRescan_IsRetriedWithoutAnotherFileChange()
    {
        await using var db = await TestDatabase.CreateAsync(); var part = AddProject("part.ipt"); var parent = AddProject("assembly.iam", part);
        _store.Failed.Add(parent); var index = Index(db.Database); await index.ScanAsync(Project);
        _store.Failed.Clear();
        var retry = await index.ScanAsync(Project);
        Assert.Equal(1, retry.Read); Assert.Empty(retry.Errors);
        Assert.Contains(await index.ComponentsAsync(parent), d => d.Path == part);
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

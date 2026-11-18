using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data;
using PdmLite.Library.Catalog;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

public sealed class ImportRecoveryTests
{
    private sealed class CallbackStore(FakePropertyStore inner, Action<string> write) : IDocumentPropertyStore
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task<DocumentProperties> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);
        public Task<DocumentProperties> ReadWithoutGeometryAsync(string path, CancellationToken cancellationToken = default) => inner.ReadWithoutGeometryAsync(path, cancellationToken);
        public async Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(path, changes, cancellationToken); write(path); }
        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string,string> map, CancellationToken cancellationToken = default) => inner.ReplaceReferencesAsync(path,map,cancellationToken);
    }
    private static string Part(TempFolders folders) => folders.AddFile("Гайка M8 ГОСТ 5915-70.ipt", "original");
    private static LibraryImportService Import(IPdmDatabase db, IDocumentPropertyStore store, TempFolders folders) =>
        new(db, store, () => new(folders.Library), NullLogger<LibraryImportService>.Instance);

    [PostgresFact]
    public async Task CancellationAfterPropertyWrite_RemovesUnregisteredCopy_AndAllowsRetry()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); using var cts = new CancellationTokenSource();
        var fake = new FakePropertyStore(); var source = Part(folders);
        var service = Import(db.Database, new CallbackStore(fake, _ => cts.Cancel()), folders);
        var session = await service.AnalyzeAsync([source]); var candidate = Assert.Single(session.Candidates); Assert.True(candidate.CanImport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(session, ct:cts.Token));
        Assert.False(File.Exists(folders.InLibrary(candidate.TargetRelativePath!))); Assert.Equal("original", File.ReadAllText(source));
        await using (var context = db.Database.CreateContext()) Assert.Empty(await context.LibraryItems.ToListAsync());
        var retry = Import(db.Database, fake, folders);
        Assert.Equal(1, (await retry.CommitAsync(await retry.AnalyzeAsync([source]))).Added);
    }

    [PostgresFact]
    public async Task OrdinaryFailure_ReportsOriginalFailureAndUnremovableCopyPath()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); var source = Part(folders);
        var service = Import(db.Database, new CallbackStore(new(), path => { File.Delete(path); Directory.CreateDirectory(path); throw new IOException("original failure"); }), folders);
        var session = await service.AnalyzeAsync([source]); var candidate = Assert.Single(session.Candidates);
        var result = await service.CommitAsync(session);
        var failure = Assert.Single(result.Failed); Assert.Contains("original failure", failure.Error);
        Assert.Contains(folders.InLibrary(candidate.TargetRelativePath!), failure.Error); Assert.Contains("Не удалось очистить", failure.Error);
        Assert.Equal(0, result.Added); Assert.Equal("original", File.ReadAllText(source));
    }

    [PostgresFact]
    public async Task CancellationWithCleanupFailure_RemainsCancellation_ButIncludesRemainingPath()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); using var cts = new CancellationTokenSource();
        var source = Part(folders);
        var service = Import(db.Database, new CallbackStore(new(), path => { File.Delete(path); Directory.CreateDirectory(path); cts.Cancel(); throw new OperationCanceledException(cts.Token); }), folders);
        var session = await service.AnalyzeAsync([source]); var candidate = Assert.Single(session.Candidates);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(session, ct:cts.Token));
        Assert.Contains(folders.InLibrary(candidate.TargetRelativePath!), error.Message); Assert.Contains("Не удалось очистить", error.Message);
        await using var context = db.Database.CreateContext(); Assert.Empty(await context.LibraryItems.ToListAsync());
    }

    private sealed class CancelAfterCommit(CancellationTokenSource stop) : DbTransactionInterceptor
    {
        public bool Enabled;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Enabled) { Enabled=false; stop.Cancel(); throw new OperationCanceledException(stop.Token); }
            return Task.CompletedTask;
        }
    }
    private sealed class Intercepted(IPdmDatabase inner, DbTransactionInterceptor interceptor) : IPdmDatabase
    {
        public bool IsConfigured => inner.IsConfigured;
        public DatabaseSettings Settings => inner.Settings;
        public void Configure(DatabaseSettings settings) => inner.Configure(settings);
        public PdmDbContext CreateContext() => new(new DbContextOptionsBuilder<PdmDbContext>()
            .UseNpgsql(inner.Settings.BuildConnectionString()).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options);
        public Task MigrateAsync(CancellationToken ct=default) => inner.MigrateAsync(ct);
        public Task<(bool Ok,string Message)> TestAsync(DatabaseSettings settings,CancellationToken ct=default) => inner.TestAsync(settings,ct);
    }
    [PostgresFact]
    public async Task CancellationReportedAfterRealCommit_DoesNotDeleteRegisteredFile()
    {
        await using var db = await TestDatabase.CreateAsync(); using var folders = new TempFolders(); using var cts = new CancellationTokenSource();
        var interceptor = new CancelAfterCommit(cts); var wrapped = new Intercepted(db.Database, interceptor);
        var service = Import(wrapped, new FakePropertyStore(), folders); var source = Part(folders);
        var session = await service.AnalyzeAsync([source]); var candidate = Assert.Single(session.Candidates); interceptor.Enabled=true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(session, ct:cts.Token));
        await using var context = db.Database.CreateContext(); var item = Assert.Single(await context.LibraryItems.ToListAsync());
        Assert.True(File.Exists(folders.InLibrary(item.RelativePath))); Assert.Equal(candidate.TargetRelativePath,item.RelativePath);
    }

    [Fact]
    public void FailedCopyPromotion_PreservesExistingTarget_AndRemovesOnlyItsTemporaryFile()
    {
        using var folders = new TempFolders(); var storage = new LibraryStorage(folders.Library); var source = Part(folders);
        var target = folders.InLibrary("target.ipt"); Directory.CreateDirectory(folders.Library); File.WriteAllText(target, "existing");
        Assert.Throws<IOException>(() => storage.CopyIn(source, "target.ipt"));
        Assert.Equal("existing",File.ReadAllText(target)); Assert.Equal("original",File.ReadAllText(source));
        Assert.Equal([target],Directory.GetFiles(folders.Library));
    }
    [Fact]
    public void ReadOnlySource_CopiesThroughStaging_WithoutChangingSourceAttributes()
    {
        using var folders = new TempFolders(); var source = Part(folders); File.SetAttributes(source, FileAttributes.ReadOnly);
        var target = new LibraryStorage(folders.Library).CopyIn(source,"target.ipt");
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal([target],Directory.GetFiles(folders.Library));
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Проверки объединения и дублирования компонентов библиотеки.</summary>
public class ItemMergeValidationTests
{
    private static (LibraryImportService Import, LibraryItemService Items) Services(TestDatabase db, FakePropertyStore store, TempFolders folders)
    {
        LibraryStorage Storage() => new(folders.Library);
        return (new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance),
                new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance));
    }

    [PostgresFact]
    public async Task Merge_RejectsKeepInRemoveList_AndPartWithAssembly()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        var part = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "p");
        var asm = folders.AddFile("Узел.iam", "a");
        store.Files[asm] = new DocumentProperties { References = [part] };
        var session = await import.AnalyzeAsync([part, asm]);
        var a = session.Candidates.Single(c => c.SourcePath == asm);
        a.CategoryId = session.Catalog.Leaves.First().Id;
        a.Manufacturer = "Свой";
        import.Validate(session);
        Assert.Equal(2, (await import.CommitAsync(session)).Added);

        var all = await items.ListAsync(await items.LoadCatalogAsync());
        var partId = all.Single(i => i.DocumentType == LibraryDocumentType.Part).Id;
        var asmId = all.Single(i => i.DocumentType == LibraryDocumentType.Assembly).Id;

        await Assert.ThrowsAsync<ArgumentException>(() => items.MergeDuplicatesAsync(partId, [partId]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => items.MergeDuplicatesAsync(partId, [asmId]));
        Assert.Equal(2, (await items.ListAsync(await items.LoadCatalogAsync())).Count);
    }

    [PostgresFact]
    public async Task DuplicateAssembly_KeepsComponentList()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, items) = Services(db, store, folders);
        var part = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "p");
        var asm = folders.AddFile("Узел крепления.iam", "a");
        store.Files[asm] = new DocumentProperties { References = [part] };
        var session = await import.AnalyzeAsync([part, asm]);
        var a = session.Candidates.Single(c => c.SourcePath == asm);
        a.CategoryId = session.Catalog.Leaves.First().Id;
        a.Manufacturer = "Свой";
        import.Validate(session);
        await import.CommitAsync(session);

        var catalog = await items.LoadCatalogAsync();
        var source = (await items.ListAsync(catalog)).Single(i => i.DocumentType == LibraryDocumentType.Assembly);
        var copyId = await items.DuplicateAsync(catalog, source.Id,
            new LibraryItemEdit("Узел крепления исп.2", LibraryItemKind.Purchased, "Свой", source.CategoryId));

        var boltId = (await items.ListAsync(catalog)).Single(i => i.DocumentType == LibraryDocumentType.Part).Id;
        var usedIn = await items.UsedInAsync(boltId);
        Assert.Equal(2, usedIn.Count); // болт — в исходной сборке и в копии
        Assert.Contains("Узел крепления исп.2", usedIn);
        Assert.NotEqual(Guid.Empty, copyId);
    }
}

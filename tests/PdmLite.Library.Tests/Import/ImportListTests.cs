using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Список подготовки к импорту: версия формата, сохранение, ручной стандарт.</summary>
public class ImportListTests
{
    private static (LibraryImportService Import, LibraryItemService Items) Services(TestDatabase db, FakePropertyStore store, TempFolders folders)
    {
        LibraryStorage Storage() => new(folders.Library);
        return (new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance),
                new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance));
    }

    [Fact]
    public void ImportList_RejectsOtherVersion_AndOverwritesOnSave()
    {
        var dir = Directory.CreateTempSubdirectory("pdmlite-list-").FullName;
        try
        {
            var path = Path.Combine(dir, "a" + ImportList.Extension);
            File.WriteAllText(path, """{"Version":2,"Items":[]}""");
            Assert.Throws<InvalidDataException>(() => ImportList.Load(path));

            new ImportList { Items = [new ImportListEntry { Path = @"C:\x.ipt", Kind = LibraryItemKind.Purchased }] }.Save(path);
            var loaded = ImportList.Load(path);
            Assert.Equal(LibraryItemKind.Purchased, Assert.Single(loaded.Items).Kind);
            Assert.Contains("\"Purchased\"", File.ReadAllText(path)); // перечисления — словами
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [PostgresFact]
    public async Task LoadList_ManualStandard_AppliedBeforeDesignationIsPrepared()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var (import, _) = Services(db, store, folders);
        var file = folders.AddFile("Кольцо 15x1.ipt", "r");
        store.Files[file] = new DocumentProperties { PartNumber = "Стопорное кольцо 15x1", Description = "Кольцо DIN 471-15x1" };

        var listPath = Path.Combine(folders.Root, "l" + ImportList.Extension);
        new ImportList
        {
            Items = [new ImportListEntry { Path = file, Source = DesignationSource.Description, Kind = LibraryItemKind.Standard, ManualStandard = " DIN  471 " }],
        }.Save(listPath);

        var session = await import.LoadListAsync(listPath);

        var c = Assert.Single(session.Candidates);
        Assert.Equal(DesignationSource.Description, c.Source);
        Assert.Equal("DIN 471", c.ManualStandard);
        Assert.Equal("DIN 471", c.Standard);
        Assert.Equal(LibraryItemKind.Standard, c.Kind);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Index;
using PdmLite.Library.Checks;
using PdmLite.Library.Import;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Сканирование индекса ссылок: вложенные проекты, параллельные запуски, геометрия, необычные пути.</summary>
public class ReferenceIndexScanTests
{
    private static ReferenceIndexService Index(TestDatabase db, FakePropertyStore store) =>
        new(db.Database, store, NullLogger<ReferenceIndexService>.Instance);

    [PostgresFact]
    public async Task Index_NestedProjects_DoNotConflict()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var part = folders.AddFile("Узел/Деталь.ipt", "p");
        var asm = folders.AddFile("Узел/Узел.iam", "a");
        var top = folders.AddFile("Изделие.iam", "t");
        store.Files[asm] = new DocumentProperties { References = [part] };
        store.Files[top] = new DocumentProperties { References = [asm] };
        var index = Index(db, store);
        var child = Path.Combine(folders.Source, "Узел");

        await index.ScanAsync(folders.Source); // весь проект
        Assert.True(await index.IsIndexedAsync(child)); // вложенная папка — уже в индексе

        var nested = await index.ScanAsync(child); // открыли подпапку как отдельный проект — без ошибки уникальности
        Assert.Equal(2, nested.Files);
        Assert.Equal(0, nested.Read);
        File.AppendAllText(part, "изменена");
        Assert.Equal(1, (await index.ScanAsync(child)).Read);
        Assert.Equal(0, (await index.ScanAsync(folders.Source)).Read); // родитель видит, что деталь уже перечитана

        var used = await index.WhereUsedAsync(part, allLevels: true);
        Assert.Equal([asm, top], used.Select(u => u.Path));
    }

    [PostgresFact]
    public async Task Index_ParallelScans_AreSerialized()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        for (var i = 0; i < 30; i++)
            folders.AddFile($"Детали/Деталь {i}.ipt", i.ToString());
        var index = Index(db, store);

        var results = await Task.WhenAll(index.ScanAsync(folders.Source), index.ScanAsync(folders.Source));

        Assert.Equal(30, results.Sum(r => r.Read)); // каждый файл прочитан один раз
        Assert.All(results, r => Assert.Empty(r.Errors));
    }

    [PostgresFact]
    public async Task Index_ReadsWithoutGeometry_ImportWithGeometry()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var store = new FakePropertyStore();
        var bolt = folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "b");

        await Index(db, store).ScanAsync(folders.Source);
        Assert.Equal(0, store.GeometryReads); // масс-свойства при сканировании проекта не считаются

        var import = new LibraryImportService(db.Database, store, () => new LibraryStorage(folders.Library), NullLogger<LibraryImportService>.Instance);
        await import.AnalyzeAsync([bolt]);
        Assert.Equal(1, store.GeometryReads); // а при добавлении в библиотеку — нужны для поиска дубликатов
    }

    [Theory]
    [InlineData("a\0b.ipt")]
    [InlineData("")]
    public void Index_Key_DoesNotThrowOnStrangePaths(string path) => _ = ReferenceIndexService.Key(path);
}

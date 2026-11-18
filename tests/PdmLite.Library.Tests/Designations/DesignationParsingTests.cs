using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Library.Designations;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Разбор стандартов в обозначениях и массовая замена в обозначениях.</summary>
public class DesignationParsingTests
{
    private static (LibraryImportService Import, LibraryItemService Items) Services(TestDatabase db, FakePropertyStore store, TempFolders folders)
    {
        LibraryStorage Storage() => new(folders.Library);
        return (new LibraryImportService(db.Database, store, Storage, NullLogger<LibraryImportService>.Instance),
                new LibraryItemService(db.Database, store, Storage, NullLogger<LibraryItemService>.Instance));
    }

    [Theory]
    [InlineData("Винт ISO 4017-M6x20", "ISO 4017")]
    [InlineData("Шпилька ГОСТ 22032-76 M10x40", "ГОСТ 22032-76")]
    [InlineData("Кольцо DIN 471-15", "DIN 471-15")] // без размера после — неоднозначно, берём как есть (можно исправить вручную)
    [InlineData("Шайба DIN 125-1 A 8,4", "DIN 125-1")]
    [InlineData("Гайка DIN 6334 M8-10", "DIN 6334")]
    public void StandardParser_MoreCases(string designation, string expected) =>
        Assert.Equal(expected, StandardParser.Find(designation));

    [PostgresFact]
    public async Task ReplaceInDesignations_RejectsEmptyAndBrokenExpressions()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var folders = new TempFolders();
        var (import, _) = Services(db, new FakePropertyStore(), folders);
        var session = await import.AnalyzeAsync([folders.AddFile("Болт M8x20 ГОСТ 7798-70.ipt", "b")]);

        Assert.Throws<ArgumentException>(() => import.ReplaceInDesignations(session, session.Candidates, "", "x", false));
        Assert.ThrowsAny<ArgumentException>(() => import.ReplaceInDesignations(session, session.Candidates, "(", "x", true));
        Assert.Equal(DesignationSource.FileName, session.Candidates[0].Source); // ничего не изменилось
    }

    [Theory]
    [InlineData("Болт M8-6gx20.109", "болт м8-6gх20.109", true)]
    [InlineData("Болт M8-6gx20.109", "Болт M8-6gx20.88", false)]
    [InlineData("Гайка DIN 6334 M8-10", "Гайка DIN 6334 M8-12", false)]
    [InlineData("Шайба A.1,6.01.08кп.016 ГОСТ 11371-78", "Шайба A.16.01.08кп.016 ГОСТ 11371-78", false)] // 1,6 мм и 16 мм
    [InlineData("Шайба A.1,6.01.08кп.016 ГОСТ 11371-78", "Шайба А.1.6.01.08кп.016 ГОСТ 11371-78", true)] // запятая и точка — одно
    [InlineData("Болт M8-6gx20-.109", "Болт M8-6gx20.109", true)] // лишний знак между числами
    [InlineData("Болт M8-6gx20.109", "Болт M8-6gx2010.9", false)]
    public void LooseKey(string a, string b, bool same) =>
        Assert.Equal(same, LibraryDuplicateService.LooseKey(a) == LibraryDuplicateService.LooseKey(b));
}

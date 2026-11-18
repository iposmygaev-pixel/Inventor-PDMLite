using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Import;
using PdmLite.Library.Rules;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Классификатор, размещение, свойства файла.</summary>
public sealed class PlacementTheoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-place3-").FullName;

    private static LibraryCatalog Catalog(params LibraryCategoryRule[] rules) => LibraryCatalog.Create(
    [
        new LibraryCategory { Id = 1, Name = "Крепёж" },
        new LibraryCategory { Id = 2, Name = "Болты", ParentId = 1 },
        new LibraryCategory { Id = 3, Name = "Электротехника" },
        new LibraryCategory { Id = 4, Name = "Разъёмы", ParentId = 3 },
    ], rules);

    private LibraryStorage Storage => new(Path.Combine(_root, "Library"));

    [Fact]
    public void Catalog_ParentCycle_DoesNotOverflow()
    {
        var catalog = LibraryCatalog.Create(
        [
            new LibraryCategory { Id = 1, Name = "А", ParentId = 2 },
            new LibraryCategory { Id = 2, Name = "Б", ParentId = 1 },
            new LibraryCategory { Id = 3, Name = "В", ParentId = 3 },
        ], []);
        Assert.Equal("Б/А", catalog.Get(1)!.Path);
        Assert.Equal("В", catalog.Get(3)!.Path);
    }

    [Fact]
    public void Catalog_UnknownParent_TreatedAsRoot() =>
        Assert.Equal("Сирота", LibraryCatalog.Create([new LibraryCategory { Id = 5, Name = "Сирота", ParentId = 99 }], []).Get(5)!.Path);

    [Fact]
    public void Catalog_DuplicateRules_LearnedWins()
    {
        var catalog = Catalog(
            new LibraryCategoryRule { Kind = LibraryRuleKind.FirstWord, Pattern = "болт", CategoryId = 4, IsLearned = true },
            new LibraryCategoryRule { Kind = LibraryRuleKind.FirstWord, Pattern = "болт", CategoryId = 2 });
        Assert.Equal(4, catalog.Suggest("Болт M8", null)!.Id);
    }

    [Fact]
    public void Catalog_RuleToDeletedCategory_SuggestsNothing() =>
        Assert.Null(Catalog(new LibraryCategoryRule { Kind = LibraryRuleKind.FirstWord, Pattern = "болт", CategoryId = 77 }).Suggest("Болт M8", null));

    [Fact]
    public void Catalog_FindByPath()
    {
        var catalog = Catalog();
        Assert.Equal(2, catalog.FindByPath("Крепёж/Болты")!.Id);
        Assert.Null(catalog.FindByPath("крепёж/болты")); // путь — точный
        Assert.Null(catalog.FindByPath(""));
        Assert.Null(catalog.Get(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Place_NoDesignation_Error(string? designation)
    {
        var place = LibraryRules.Place(Catalog(), Storage, designation, LibraryItemKind.Standard, null, 2, ".ipt");
        Assert.Contains(place.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains("обозначение"));
        Assert.Null(place.RelativePath);
    }

    [Fact]
    public void Place_StandardWithoutStandard_Error_ButPathBuilt()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Болт M8", LibraryItemKind.Standard, null, 2, ".ipt");
        Assert.Contains(place.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains("стандарт"));
    }

    [Fact]
    public void Place_ManualStandard_UsedAsFolder_AndInfo()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Кольцо A15 DIN 471", LibraryItemKind.Standard, null, 2, ".ipt", "  DIN   471  ");
        Assert.Equal("DIN 471", place.Standard);
        Assert.Equal("Крепёж/Болты/DIN 471/Кольцо A15 DIN 471.ipt", place.RelativePath);
        Assert.DoesNotContain(place.Issues, i => i.Severity == ImportSeverity.Info); // совпадает с распознанным
        var other = LibraryRules.Place(Catalog(), Storage, "Кольцо A15 DIN 471", LibraryItemKind.Standard, null, 2, ".ipt", "ГОСТ 13942-86");
        Assert.Contains(other.Issues, i => i.Severity == ImportSeverity.Info && i.Message.Contains("ГОСТ 13942-86"));
    }

    [Fact]
    public void Place_Purchased_IgnoresStandard_WarnsWithoutMaker()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Разъём X1 ГОСТ 1-80", LibraryItemKind.Purchased, "  ", 4, ".ipt", "ГОСТ 1-80");
        Assert.Null(place.Standard);
        Assert.Null(place.Manufacturer);
        Assert.Contains(LibraryStorage.NoManufacturerFolder, place.RelativePath);
        Assert.Contains(place.Issues, i => i.Severity == ImportSeverity.Warning);
    }

    [Fact]
    public void Place_MakerTrimmed()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Разъём X1", LibraryItemKind.Purchased, "  ABB ", 4, ".ipt");
        Assert.Equal("ABB", place.Manufacturer);
        Assert.Equal("Электротехника/Разъёмы/ABB/Разъём X1.ipt", place.RelativePath);
        Assert.Empty(place.Issues);
    }

    [Theory]
    [InlineData(null, "Не выбран")]
    [InlineData(99, "Не выбран")]
    [InlineData(1, "раздел; выберите группу")]
    public void Place_BadCategory_Error(int? category, string message)
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Болт M8 ГОСТ 7798-70", LibraryItemKind.Standard, null, category, ".ipt");
        Assert.Contains(place.Issues, i => i.Severity == ImportSeverity.Error && i.Message.Contains(message));
        Assert.Null(place.RelativePath);
    }

    [Fact]
    public void Place_TooLongPath_Error()
    {
        var place = LibraryRules.Place(Catalog(), Storage, "Болт " + new string('x', 260) + " ГОСТ 7798-70", LibraryItemKind.Standard, null, 2, ".ipt");
        Assert.Contains(place.Issues, i => i.Message.Contains("Слишком длинный путь"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("ГОСТ   7798-70", "ГОСТ 7798-70")]
    [InlineData(" DIN 471 ", "DIN 471")]
    public void NormalizeStandard(string? input, string? expected) => Assert.Equal(expected, LibraryRules.NormalizeStandard(input));

    [Theory]
    [InlineData(LibraryItemKind.Standard, "StandardProducts")]
    [InlineData(LibraryItemKind.Purchased, "OtherProducts")]
    public void SpecSectionFor(LibraryItemKind kind, string expected) => Assert.Equal(expected, LibraryProperties.SpecSectionFor(kind));

    [Theory]
    [InlineData("StandardProducts", LibraryItemKind.Standard)]
    [InlineData(" standardproducts ", LibraryItemKind.Standard)]
    [InlineData("OtherProducts", LibraryItemKind.Purchased)]
    [InlineData("OTHERPRODUCTS", LibraryItemKind.Purchased)]
    [InlineData("Details", null)]
    [InlineData("", null)]
    public void KindFromSpecSection(string value, LibraryItemKind? expected) =>
        Assert.Equal(expected, LibraryProperties.KindFromSpecSection(new Dictionary<string, string> { [LibraryProperties.SpecSection] = value }));

    [Fact]
    public void KindFromSpecSection_NoProperty_Null() => Assert.Null(LibraryProperties.KindFromSpecSection(new Dictionary<string, string>()));

    [Fact]
    public void For_WritesAllFields_IdOptional()
    {
        var id = Guid.NewGuid();
        var changes = LibraryProperties.For("Болт M8", LibraryItemKind.Standard, null, null, "Крепёж/Болты", id);
        Assert.Equal("Болт M8", changes.PartNumber);
        Assert.Equal("Болт M8", changes.Description);
        Assert.Equal("Болт M8", changes.Title);
        Assert.True(changes.RemoveContentCenterLink);
        Assert.Equal("", changes.Custom[LibraryProperties.Standard]);
        Assert.Equal("", changes.Custom[LibraryProperties.Manufacturer]);
        Assert.Equal(id.ToString(), changes.Custom[LibraryProperties.LibraryId]);
        Assert.False(LibraryProperties.For("a", LibraryItemKind.Purchased, null, "ABB", "x").Custom.ContainsKey(LibraryProperties.LibraryId));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

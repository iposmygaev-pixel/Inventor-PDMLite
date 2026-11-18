using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Checks;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Подбор замены по обозначению.</summary>
public class LibraryMatcherTheoryTests
{
    private static LibraryItemSummary Item(string designation, string ext = ".ipt") =>
        new(Guid.NewGuid(), designation, LibraryItemKind.Standard, LibraryDocumentType.Part, null, null, 1, "Крепёж/Болты",
            $"Крепёж/Болты/{designation}{ext}", 1, DateTime.UtcNow, "test");

    [Theory]
    [InlineData("Болт M8-6gx30.109 ГОСТ 7798-70")]
    [InlineData("болт m8-6gx30.109 гост 7798-70")]
    [InlineData("Болт  M8-6gx30.109  ГОСТ 7798-70")]
    [InlineData("Болт М8-6gх30.109 ГОСТ 7798-70")] // кириллические М и х
    public void Exact_IgnoresCaseSpacesAndLookalikes(string query)
    {
        var bolt = Item("Болт M8-6gx30.109 ГОСТ 7798-70");
        var found = new LibraryMatcher([bolt]).Find(query);
        Assert.True(found[0].Exact);
        Assert.Same(bolt, found[0].Item);
    }

    [Fact]
    public void EmptyLibrary_NothingFound() => Assert.Empty(new LibraryMatcher([]).Find("Болт M8 ГОСТ 7798-70"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")] // без наименования
    public void NoNameOrEmpty_NothingSimilar(string query) =>
        Assert.Empty(new LibraryMatcher([Item("Болт M8 ГОСТ 7798-70")]).Find(query));

    [Fact]
    public void Max_LimitsResults()
    {
        var items = Enumerable.Range(1, 20).Select(i => Item($"Болт M8-6gx30.{i} ГОСТ 7798-70")).ToList();
        var matcher = new LibraryMatcher(items);
        Assert.Equal(10, matcher.Find("Болт M8x30 ГОСТ 7798-70").Count);
        Assert.Equal(3, matcher.Find("Болт M8x30 ГОСТ 7798-70", max: 3).Count);
        Assert.Empty(matcher.Find("Болт M8x30 ГОСТ 7798-70", max: 0));
    }

    [Fact]
    public void SameKeyTwice_FirstIsExact_SecondNotDuplicated()
    {
        var first = Item("Болт M8 ГОСТ 7798-70");
        var second = Item("болт m8 гост 7798-70");
        var found = new LibraryMatcher([first, second]).Find("Болт M8 ГОСТ 7798-70");
        Assert.Same(first, found[0].Item);
        Assert.Equal(found.Count, found.Select(f => f.Item).Distinct().Count());
    }

    [Fact]
    public void LeadingZeros_CompareByValue()
    {
        // В «08» нет числа 1 из «8.01» — не похож; наоборот — похож (лишнее число 1).
        Assert.Empty(new LibraryMatcher([Item("Шайба 08 ГОСТ 11371-78")]).Find("Шайба 8.01 ГОСТ 11371-78"));
        var found = new LibraryMatcher([Item("Шайба 8.01 ГОСТ 11371-78")]).Find("Шайба 08 ГОСТ 11371-78");
        Assert.Single(found);
        Assert.Equal(1, found[0].Extra);
    }

    [Fact]
    public void SimilarSortedByExtraThenNaturally()
    {
        var a = Item("Болт M8-6gx30.88 ГОСТ 7798-70");
        var b = Item("Болт M8-6gx30.109 ГОСТ 7798-70");
        var c = Item("Болт M8-6gx30.109.40X ГОСТ 7798-70");
        var found = new LibraryMatcher([c, b, a]).Find("Болт M8x30 ГОСТ 7798-70");
        Assert.Equal([a, b, c], found.Select(f => f.Item));
    }

    [Fact]
    public void WithoutStandard_MatchesOnlyItemsWithoutStandard()
    {
        var withStandard = Item("Кнопка 22 ГОСТ 1234-80");
        var plain = Item("Кнопка 22 красная 1");
        var found = new LibraryMatcher([withStandard, plain]).Find("Кнопка 22");
        Assert.Equal([plain], found.Select(f => f.Item));
    }
}

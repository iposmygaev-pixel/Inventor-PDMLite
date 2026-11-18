using System.Text.Json;
using CollectComponents;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;

namespace PdmLite.Core.Tests;

/// <summary>Естественный порядок — граничные случаи.</summary>
public class NaturalComparerTheoryTests
{
    [Theory]
    [InlineData("Болт M6", "Болт M8", -1)]
    [InlineData("Болт M8", "Болт M10", -1)]
    [InlineData("Болт M10", "Болт M8", 1)]
    [InlineData("Болт M10x20", "Болт M10x100", -1)]
    [InlineData("Шайба 1,6", "Шайба 16", -1)]
    [InlineData("Шайба 1.6", "Шайба 1.10", -1)] // «.» — не десятичная точка, части сравниваются как числа
    [InlineData("a", "A", 1)] // равны без учёта регистра — порядок устойчивый, по коду символа
    [InlineData("A", "a", -1)]
    [InlineData("abc", "abd", -1)]
    [InlineData("ab", "abc", -1)]
    [InlineData("1", "a", -1)] // цифры раньше букв
    [InlineData("0", "00", -1)]
    [InlineData("0", "1", -1)]
    [InlineData("000", "0", 1)]
    [InlineData("1a", "1b", -1)]
    [InlineData("2a", "10a", -1)]
    [InlineData("ГОСТ 7798-70", "ГОСТ 7805-70", -1)]
    [InlineData("ГОСТ 2.101", "ГОСТ 2.11", 1)]
    [InlineData("Ж", "е", 1)]
    [InlineData("x 1", "x  1", -1)] // разное число пробелов — разные строки, сравнение не падает
    [InlineData("18446744073709551616", "18446744073709551615", 1)] // больше ulong
    [InlineData("", "", 0)]
    public void Compare(string x, string y, int expected) =>
        Assert.Equal(expected, Math.Sign(NaturalComparer.Instance.Compare(x, y)));

    [Theory]
    [InlineData("a10", "a10")]
    [InlineData("Болт M8", "Болт M8")]
    public void Equal_IsZero_AndSymmetric(string x, string y)
    {
        Assert.Equal(0, NaturalComparer.Instance.Compare(x, y));
        Assert.Equal(0, NaturalComparer.Instance.Compare(y, x));
    }

    [Fact]
    public void NonGenericComparer_HandlesObjectsAndNulls()
    {
        System.Collections.IComparer comparer = NaturalComparer.Instance;
        Assert.True(comparer.Compare(2, 10) < 0); // числа — по ToString
        Assert.True(comparer.Compare(null, "a") < 0);
        Assert.True(comparer.Compare("a", null) > 0);
        Assert.Equal(0, comparer.Compare(null, null));
    }

    [Fact]
    public void Antisymmetric_OnSample()
    {
        string[] sample = ["", "0", "00", "1", "01", "a", "A", "a1", "a01", "a1b", "a10", "Болт M8", "болт M08", "x99", "x100"];
        foreach (var x in sample)
        foreach (var y in sample)
            Assert.Equal(-Math.Sign(NaturalComparer.Instance.Compare(y, x)), Math.Sign(NaturalComparer.Instance.Compare(x, y)));
    }

    [Fact]
    public void Transitive_OnSample()
    {
        string[] sample = ["0", "00", "1", "01", "a", "A", "a1", "a01", "a1b", "a10", "b", "B2", "b10"];
        var c = NaturalComparer.Instance;
        foreach (var x in sample)
        foreach (var y in sample)
        foreach (var z in sample)
            if (c.Compare(x, y) <= 0 && c.Compare(y, z) <= 0)
                Assert.True(c.Compare(x, z) <= 0, $"{x} ≤ {y} ≤ {z}");
    }
}

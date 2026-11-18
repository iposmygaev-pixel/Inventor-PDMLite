using System.Text.Json;
using CollectComponents;
using PdmLite.Core.Documents;
using PdmLite.Core.Scripts;

namespace PdmLite.Core.Tests;

/// <summary>Имена вхождений.</summary>
public class OccurrenceNamingTheoryTests
{
    [Theory]
    [InlineData("Болт:1", "Болт", true)]
    [InlineData("Болт:0", "Болт", true)]
    [InlineData("Болт:999", "Болт", true)]
    [InlineData("Болт:", "Болт", false)]
    [InlineData("Болт :1", "Болт", false)]
    [InlineData("болт:1", "Болт", false)] // регистр имени важен для обозревателя
    [InlineData("Болт:1:2", "Болт", false)]
    [InlineData("Болт (1.5):1", "Болт (1.5)", true)] // символы регулярных выражений в имени
    [InlineData("Болт (125):1", "Болт (1.5)", false)]
    [InlineData("a+b:1", "a+b", true)]
    [InlineData("aab:1", "a+b", false)]
    [InlineData("Болт:-1", "Болт", false)]
    public void IsDefault(string name, string baseName, bool expected) =>
        Assert.Equal(expected, OccurrenceNaming.IsDefault(name, baseName));

    [Fact]
    public void Plan_TwoNewNames_GetConsecutiveFreeNumbers()
    {
        var plan = OccurrenceNaming.Plan(["Болт:2", "Старый:1", "Старый:2"], [("Старый:1", "Болт"), ("Старый:2", "Болт")]);
        Assert.Equal([("Старый:1", "Болт:1"), ("Старый:2", "Болт:3")], plan);
    }

    [Fact]
    public void Plan_NameBeingRenamed_FreesItsNumber()
    {
        // «Болт:1» — имя кандидата под другой документ: освобождается и может достаться другому.
        var plan = OccurrenceNaming.Plan(["Болт:1"], [("Болт:1", "Гайка")]);
        Assert.Equal([("Болт:1", "Гайка:1")], plan);
    }

    [Fact]
    public void Plan_NoCandidates_Empty() => Assert.Empty(OccurrenceNaming.Plan(["a:1"], []));

    [Fact]
    public void Plan_WhitespaceBaseName_Skipped() => Assert.Empty(OccurrenceNaming.Plan(["x"], [("x", "   ")]));

    [Fact]
    public void Plan_CaseInsensitiveUsedNames()
    {
        var plan = OccurrenceNaming.Plan(["БОЛТ:1", "x"], [("x", "Болт")]);
        Assert.Equal([("x", "Болт:2")], plan);
    }
}

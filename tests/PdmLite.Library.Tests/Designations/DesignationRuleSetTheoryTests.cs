using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Checks;
using PdmLite.Library.Import;
using PdmLite.Library.Items;
using PdmLite.Library.Rules;

namespace PdmLite.Library.Tests;

/// <summary>Правила замены.</summary>
public class DesignationRuleSetTheoryTests
{
    private static DesignationRule Rule(string pattern, string? replacement, bool regex = false, int order = 0, int id = 0, bool enabled = true) =>
        new() { Id = id, Pattern = pattern, Replacement = replacement!, IsRegex = regex, SortOrder = order, Enabled = enabled };

    [Theory]
    [InlineData("Болт_M8", "_", " ", false, "Болт M8")]
    [InlineData("БОЛТ_M8", "болт", "Болт", false, "БОЛТ_M8")] // обычный текст — с учётом регистра
    [InlineData("M8х20", @"(\d)х(\d)", "$1x$2", true, "M8x20")]
    [InlineData("M8  x  20", @"\s+", " ", true, "M8 x 20")]
    [InlineData("abc", "b", "", false, "ac")]
    [InlineData("a.c", ".", "-", false, "a-c")] // точка в обычном тексте — точка
    [InlineData("abc", ".", "-", true, "---")] // в регулярном — любой символ
    [InlineData("Болт", "$", "!", false, "Болт")]
    [InlineData("цена $5", "$5", "5$", false, "цена 5$")]
    public void Apply(string input, string pattern, string replacement, bool regex, string expected) =>
        Assert.Equal(expected, new DesignationRuleSet([Rule(pattern, replacement, regex)]).ApplyReplacements(input).Result);

    [Fact]
    public void NullReplacement_Deletes()
    {
        Assert.Equal("ac", new DesignationRuleSet([Rule("b", null)]).ApplyReplacements("abc").Result);
        Assert.Equal("ac", new DesignationRuleSet([Rule("b", null, regex: true)]).ApplyReplacements("abc").Result);
    }

    [Fact]
    public void SameSortOrder_ById()
    {
        var set = new DesignationRuleSet([Rule("b", "c", id: 2), Rule("a", "b", id: 1)]);
        Assert.Equal("c", set.ApplyReplacements("a").Result); // сначала a→b (Id 1), затем b→c
    }

    [Fact]
    public void DisabledInvalidRule_NotReportedAsInvalid()
    {
        var set = new DesignationRuleSet([Rule("(", "", regex: true, enabled: false)]);
        Assert.Empty(set.Invalid);
        Assert.False(set.HasReplacements);
    }

    [Fact]
    public void Empty_HasNoReplacements() => Assert.False(DesignationRuleSet.Empty.HasReplacements);

    [Fact]
    public void CatastrophicRegex_TimesOut_RuleSkipped_NextApplied()
    {
        var set = new DesignationRuleSet([Rule("(a+)+$", "x", regex: true, order: 0), Rule("b", "c", order: 1)]);
        var input = new string('a', 40) + "b";
        var (result, applied) = set.ApplyReplacements(input);
        Assert.Equal(new string('a', 40) + "c", result);
        Assert.Single(applied);
    }

    [Theory]
    [InlineData("Комментарий", "a", "b", "Комментарий")]
    [InlineData("  ", "a", "b", "«a» → «b»")]
    [InlineData(null, "a", "", "«a» → «»")]
    public void Describe(string? comment, string pattern, string replacement, string expected) =>
        Assert.Equal(expected, DesignationRuleSet.Describe(new DesignationRule { Pattern = pattern, Replacement = replacement, Comment = comment }));

    [Theory]
    [InlineData("[", true)]
    [InlineData("a{2", false)] // .NET допускает как обычный текст
    [InlineData("(?<x>a)", false)]
    [InlineData("*", true)]
    public void Validate_Regex(string pattern, bool error) =>
        Assert.Equal(error, DesignationRuleSet.Validate(new DesignationRule { Pattern = pattern, IsRegex = true }) is not null);
}

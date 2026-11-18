using System.Text.Json;
using CollectComponents;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;

namespace PdmLite.Core.Tests;

/// <summary>Имя файла по правилу.</summary>
public class FileNameBuilderTheoryTests
{
    [Theory]
    [InlineData("АБВГ.301.001", "Рама", null, "", "АБВГ.301.001 Рама")]
    [InlineData("АБВГ.301.001", "Рама", "", ".iam", "АБВГ.301.001 Рама.iam")]
    [InlineData("АБВГ.301.001", "Рама", null, " .iam ", "АБВГ.301.001 Рама.iam")]
    [InlineData("АБВГ.301.001 ", "Рама", "СБ ", "idw", "АБВГ.301.001СБ Рама.idw")]
    [InlineData("АБВГ/301", "Рама", null, ".ipt", "АБВГ-301 Рама.ipt")]
    [InlineData("АБВГ.301.001", "***", null, ".ipt", "АБВГ.301.001.ipt")]
    [InlineData("АБВГ.301.001", "Рама\tсварная", null, ".ipt", "АБВГ.301.001 Рама сварная.ipt")]
    [InlineData("АБВГ.301.001", "Рама\nсварная", null, ".ipt", "АБВГ.301.001 Рама сварная.ipt")]
    public void Build(string designation, string name, string? code, string ext, string expected) =>
        Assert.Equal(expected, FileNameBuilder.Build(designation, name, code, ext));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("***")]
    [InlineData("...")]
    [InlineData("- -")]
    public void Build_NoDesignation_Throws_EvenWithDocumentCode(string designation) =>
        Assert.Throws<ArgumentException>(() => FileNameBuilder.Build(designation, "Рама", "СБ", ".idw"));

    [Theory]
    [InlineData("", "")]
    [InlineData("a<b>c", "abc")]
    [InlineData("a|b", "ab")]
    [InlineData("a\u0001b", "ab")]
    [InlineData("a - b", "a - b")] // пробелы в наименовании сохраняются
    [InlineData("a--b", "a-b")] // повторы дефиса схлопываются
    [InlineData("..a..", "a")]
    [InlineData("a\\\\b", "a-b")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, FileNameBuilder.Normalize(input));
}

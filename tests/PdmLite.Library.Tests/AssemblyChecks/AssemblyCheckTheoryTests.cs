using Microsoft.Extensions.Logging.Abstractions;
using PdmLite.Core.Documents;
using PdmLite.Data.Library;
using PdmLite.Library.Catalog;
using PdmLite.Library.Checks;
using PdmLite.Library.Items;
using PdmLite.Library.Storage;

namespace PdmLite.Library.Tests;

/// <summary>Проверка сборки.</summary>
public sealed class AssemblyCheckTheoryTests
{
    private static readonly string LibRoot = Path.Combine(Path.GetTempPath(), "pdmlite-check3-lib");
    private static readonly LibraryStorage Storage = new(LibRoot);

    private static LibraryItemSummary Item(string designation) =>
        new(Guid.NewGuid(), designation, LibraryItemKind.Standard, LibraryDocumentType.Part, null, null, 1, "Крепёж/Болты",
            $"Крепёж/Болты/{designation}.ipt", 1, DateTime.UtcNow, "test");

    private static AssemblyComponentInfo Component(string name, string? partNumber = null, bool cc = false, bool missing = false, string? folder = null) =>
        new(Path.Combine(folder ?? Path.Combine(Path.GetTempPath(), "proj"), name), partNumber, cc, 1, missing);

    [Fact]
    public void InLibrary_Counted_NotReported()
    {
        var result = AssemblyCheck.Run([Component("Болт.ipt", folder: Path.Combine(LibRoot, "Крепёж")), Component("Своя.ipt")], [], Storage);
        Assert.Equal(1, result.InLibrary);
        Assert.Equal(2, result.Total);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void OwnPartMissing_StillReported()
    {
        var check = Assert.Single(AssemblyCheck.Run([Component("Своя.ipt", missing: true)], [], Storage).Findings);
        Assert.Equal(ComponentFinding.MissingFile, check.Finding);
        Assert.False(check.CanReplace);
        Assert.Contains("Замена ссылок", check.Message);
    }

    [Fact]
    public void CopyOfLibraryItem_ByFileNameWhenNoPartNumber()
    {
        var bolt = Item("Болт M8 ГОСТ 7798-70");
        var check = Assert.Single(AssemblyCheck.Run([Component("Болт M8 ГОСТ 7798-70.ipt", partNumber: "  ")], [bolt], Storage).Findings);
        Assert.Equal(ComponentFinding.CopyOfLibraryItem, check.Finding);
        Assert.True(check.IsExact);
        Assert.Same(bolt, check.Replacement);
    }

    [Fact]
    public void WindowsPath_FileNameTakenOnAnyPlatform()
    {
        var component = new AssemblyComponentInfo(@"C:\Проект\Болт M8 ГОСТ 7798-70.ipt", null, false, 1);
        var check = Assert.Single(AssemblyCheck.Run([component], [], Storage).Findings);
        Assert.Equal("Болт M8 ГОСТ 7798-70", check.Designation);
        Assert.Equal(ComponentFinding.StandardNotInLibrary, check.Finding);
    }

    [Fact]
    public void Findings_OrderedByKindThenNaturally()
    {
        var result = AssemblyCheck.Run(
        [
            Component("x1.ipt", "Болт M10 ГОСТ 7798-70"),
            Component("x2.ipt", "Болт M8 ГОСТ 7798-70"),
            Component("x3.ipt", "Шайба 8 ГОСТ 11371-78", cc: true),
            Component("x4.ipt", missing: true),
        ], [], Storage);
        Assert.Equal([ComponentFinding.MissingFile, ComponentFinding.ContentCenter, ComponentFinding.StandardNotInLibrary, ComponentFinding.StandardNotInLibrary],
            result.Findings.Select(f => f.Finding));
        Assert.Equal("Болт M8 ГОСТ 7798-70", result.Findings[2].Designation);
    }

    [Theory]
    [InlineData(ComponentFinding.ContentCenter, false, "нет")]
    [InlineData(ComponentFinding.StandardNotInLibrary, false, "Добавьте")]
    [InlineData(ComponentFinding.CopyOfLibraryItem, false, "Копия")]
    public void Message_WithoutCandidates(ComponentFinding finding, bool _, string text) =>
        Assert.Contains(text, new ComponentCheck(Component("a.ipt"), "a", finding, []).Message);

    [Fact]
    public void Empty_Assembly()
    {
        var result = AssemblyCheck.Run([], [Item("Болт M8 ГОСТ 7798-70")], Storage);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Findings);
    }
}

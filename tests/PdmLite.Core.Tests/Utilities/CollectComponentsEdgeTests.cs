using System.Text.Json;
using CollectComponents;
using PdmLite.Core.Naming;
using PdmLite.Core.Scripts;

namespace PdmLite.Core.Tests;

/// <summary>Утилита сбора — циклы, повторы, сбои чтения, повторный запуск.</summary>
public sealed class CollectComponentsEdgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-collect3-").FullName;
    private string Source => Path.Combine(_root, "src");
    private string Target => Path.Combine(_root, "out");

    private string Add(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Source, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
        return path;
    }

    private sealed class Refs(Dictionary<string, string[]> map, string? failOn = null) : IReferenceReader
    {
        public List<string> Reads { get; } = [];

        public IReadOnlyList<DocumentReference> Read(string path)
        {
            var name = Path.GetFileName(path);
            Reads.Add(name);
            if (name == failOn)
                throw new InvalidOperationException("файл повреждён");
            return map.TryGetValue(name, out var refs) ? refs.Select(r => new DocumentReference(r, null)).ToList() : [];
        }
    }

    private static List<string> Names(CollectResult result) => result.Found.Select(f => Path.GetFileName(f.Path)).ToList();

    [Theory]
    [InlineData("Блок.iam\r", "Блок.iam")]
    [InlineData("D:/Папка/Блок.iam", "Блок.iam")]
    [InlineData("  \"D:\\Папка\\Блок.iam\"  ", "Блок.iam")]
    [InlineData("Блок.iam\t\tлишнее", "Блок.iam")]
    [InlineData("\\\\server\\share\\Блок.iam", "Блок.iam")]
    [InlineData("Имя с пробелами.ipt", "Имя с пробелами.ipt")]
    [InlineData("Без расширения", "Без расширения")]
    public void ParseNames_Single(string line, string expected) => Assert.Equal([expected], Collector.ParseNames([line]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\"\"")]
    [InlineData("D:\\Папка\\")]
    public void ParseNames_NothingUseful(string line) => Assert.Empty(Collector.ParseNames([line]));

    [Theory]
    [InlineData("a.ipt", true)]
    [InlineData("a.IAM", true)]
    [InlineData("a.idw", true)]
    [InlineData("a.dwg", true)]
    [InlineData("a.ipn", true)]
    [InlineData("a.step", false)]
    [InlineData("a.pdf", false)]
    [InlineData("a", false)]
    public void IsInventorFile(string path, bool expected) => Assert.Equal(expected, Collector.IsInventorFile(path));

    [Fact]
    public void Cycle_AB_Terminates_EachOnce()
    {
        Add("A.iam");
        Add("B.iam");
        var refs = new Refs(new() { ["A.iam"] = ["B.iam"], ["B.iam"] = ["A.iam"] });
        var result = new Collector(Source).Collect(["A.iam"], refs, _ => { });
        Assert.Equal(["A.iam", "B.iam"], Names(result));
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void SelfReference_Ignored()
    {
        Add("A.iam");
        var result = new Collector(Source).Collect(["A.iam"], new Refs(new() { ["A.iam"] = ["A.iam"] }), _ => { });
        Assert.Equal(["A.iam"], Names(result));
    }

    [Fact]
    public void ReaderThrows_WarningAndFileStillCopied_OthersContinue()
    {
        Add("A.iam");
        Add("B.iam");
        Add("C.ipt");
        var refs = new Refs(new() { ["B.iam"] = ["C.ipt"] }, failOn: "A.iam");
        var log = new List<string>();

        var result = new Collector(Source).Collect(["A.iam", "B.iam"], refs, log.Add);

        Assert.Equal(["A.iam", "B.iam", "C.ipt"], Names(result));
        Assert.Contains(result.Warnings, w => w.Contains("A.iam") && w.Contains("файл повреждён"));
        Assert.Contains(log, l => l.Contains("НЕ прочитаны"));
    }

    [Fact]
    public void NonInventorFile_NotReadForReferences()
    {
        Add("Схема.pdf");
        var refs = new Refs([]);
        var result = new Collector(Source).Collect(["Схема.pdf"], refs, _ => { });
        Assert.Equal(["Схема.pdf"], Names(result));
        Assert.Empty(refs.Reads);
    }

    [Fact]
    public void ListedTwice_DifferentCase_CopiedOnce()
    {
        Add("A.iam");
        var result = new Collector(Source).Collect(["A.iam", "a.IAM"], null, _ => { });
        Assert.Single(result.Found);
    }

    [Fact]
    public void DependencyAlsoListed_StaysOnTop_NotInSubfolder()
    {
        Add("A.iam");
        Add("B.ipt");
        var result = new Collector(Source).Collect(["A.iam", "B.ipt"], new Refs(new() { ["A.iam"] = ["B.ipt"] }), _ => { });
        Assert.All(result.Found, f => Assert.Null(f.Root));
    }

    [Fact]
    public void ListedComponentMissing_DependencyOfAnother_StillFoundBelow()
    {
        Add("A.iam");
        Add("B.ipt");
        var result = new Collector(Source).Collect(["Нет.iam", "A.iam"], new Refs(new() { ["A.iam"] = ["B.ipt"] }), _ => { });
        Assert.Equal(["A.iam", "B.ipt"], Names(result));
        Assert.Equal(["Нет.iam (из списка)"], result.Missing);
    }

    [Fact]
    public void MissingDependency_ReportedOnce_EvenIfReferencedTwice()
    {
        Add("A.iam");
        Add("B.iam");
        var refs = new Refs(new() { ["A.iam"] = ["Пропал.ipt"], ["B.iam"] = ["Пропал.ipt"] });
        var result = new Collector(Source).Collect(["A.iam", "B.iam"], refs, _ => { });
        Assert.Single(result.Missing);
    }

    [Fact]
    public void OldVersions_AnyCase_Skipped()
    {
        Add("oldversions/A.iam");
        Add("OLDVERSIONS/B.iam");
        Add("Папка/OldVersions/C.iam");
        var collector = new Collector(Source);
        Assert.Equal(0, collector.FileCount);
    }

    [Fact]
    public void FolderNamedLikeOldVersionsPrefix_NotSkipped()
    {
        Add("OldVersionsArchive/A.iam");
        Assert.Equal(1, new Collector(Source).FileCount);
    }

    [Fact]
    public void SourceWithTrailingSeparator_Normalized()
    {
        Add("A.iam");
        var collector = new Collector(Source + Path.DirectorySeparatorChar);
        var result = collector.Collect(["A.iam"], null, _ => { });
        var report = File.ReadAllText(collector.Copy(result, Target));
        Assert.Contains("Где искали: " + Source + Environment.NewLine, report);
    }

    [Fact]
    public void SourceMissing_Throws() =>
        Assert.Throws<DirectoryNotFoundException>(() => new Collector(Path.Combine(_root, "нет")));

    [Fact]
    public void DeepChain_AllInFolderOfTop()
    {
        Add("A.iam");
        Add("B.iam");
        Add("C.iam");
        Add("D.ipt");
        var refs = new Refs(new() { ["A.iam"] = ["B.iam"], ["B.iam"] = ["C.iam"], ["C.iam"] = ["D.ipt"] });
        var result = new Collector(Source).Collect(["A.iam"], refs, _ => { });
        Assert.Equal([null, "A.iam", "A.iam", "A.iam"], result.Found.Select(f => f.Root));
        Assert.Equal([null, "A.iam", "B.iam", "C.iam"], result.Found.Select(f => f.Reason));
    }

    [Fact]
    public void RelativeTarget_TopAndComposition()
    {
        Assert.Equal("A.iam", new FoundFile(Path.Combine("x", "A.iam"), null).RelativeTarget);
        Assert.Equal(Path.Combine("A", "B.ipt"), new FoundFile(Path.Combine("x", "B.ipt"), "A.iam", "A.iam").RelativeTarget);
    }

    [Fact]
    public void Copy_Rerun_OverwritesReadOnlyCopyFromPreviousRun()
    {
        Add("A.iam");
        var collector = new Collector(Source);
        var result = collector.Collect(["A.iam"], null, _ => { });
        collector.Copy(result, Target);
        var copy = Path.Combine(Target, "A.iam");
        File.SetAttributes(copy, FileAttributes.ReadOnly); // например, вернули из архива

        collector.Copy(result, Target);

        Assert.True(File.Exists(copy));
        Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Copy_TargetIsSourceFolder_DoesNotFailOnSameFile()
    {
        var a = Add("A.iam");
        var collector = new Collector(Source);
        var result = collector.Collect(["A.iam"], null, _ => { });

        collector.Copy(result, Source);

        Assert.Equal("A.iam", File.ReadAllText(a));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("sub", true)]
    [InlineData("sub/deeper", true)]
    [InlineData("..", false)]
    [InlineData("../out", false)]
    [InlineData("../src2", false)] // общее начало имени — ещё не «внутри»
    public void IsInsideSource(string relative, bool expected)
    {
        Directory.CreateDirectory(Source);
        Assert.Equal(expected, new Collector(Source).IsInsideSource(Path.Combine(Source, relative)));
    }

    [Fact]
    public void Copy_ReportListsWarnings_AndDependencyReasons()
    {
        Add("A.iam");
        Add("B.ipt");
        Add("x/C.ipt");
        Add("y/C.ipt");
        var collector = new Collector(Source);
        var result = collector.Collect(["A.iam", "C.ipt"], new Refs(new() { ["A.iam"] = ["B.ipt"] }), _ => { });

        var report = File.ReadAllText(collector.Copy(result, Target));

        Assert.Contains("СКОПИРОВАНО (3)", report);
        Assert.Contains("[зависимость A.iam]", report);
        Assert.Contains("ЗАМЕЧАНИЯ (1)", report);
        Assert.Contains("НЕ НАЙДЕНО (0)", report);
    }

    [Fact]
    public void Copy_NoWarnings_NoWarningsSection()
    {
        Add("A.iam");
        var collector = new Collector(Source);
        var report = File.ReadAllText(collector.Copy(collector.Collect(["A.iam"], null, _ => { }), Target));
        Assert.DoesNotContain("ЗАМЕЧАНИЯ", report);
    }

    [Fact]
    public void Collect_EmptyList_NothingFound()
    {
        Add("A.iam");
        var result = new Collector(Source).Collect([], new Refs([]), _ => { });
        Assert.Empty(result.Found);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void Collect_SharedSubassembly_ReadOnce_PlacedUnderFirst()
    {
        Add("A.iam");
        Add("B.iam");
        Add("S.iam");
        Add("P.ipt");
        var refs = new Refs(new() { ["A.iam"] = ["S.iam"], ["B.iam"] = ["S.iam"], ["S.iam"] = ["P.ipt"] });
        var result = new Collector(Source).Collect(["A.iam", "B.iam"], refs, _ => { });
        Assert.Equal("A.iam", result.Found.Single(f => f.Path.EndsWith("P.ipt")).Root);
        Assert.Equal(1, refs.Reads.Count(r => r == "S.iam"));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}

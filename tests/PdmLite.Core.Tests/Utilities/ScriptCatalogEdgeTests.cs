using System.Text.Json;
using CollectComponents;
using PdmLite.Core.Documents;
using PdmLite.Core.Naming;
using PdmLite.Core.Scripts;

namespace PdmLite.Core.Tests;

/// <summary>Каталог внешних утилит.</summary>
public sealed class ScriptCatalogEdgeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdmlite-scripts3-").FullName;

    private string Add(string relative, string content = "x")
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Folder => Path.Combine(_root, "Scripts");
    private string Bundled => Path.Combine(_root, "bundled");

    [Theory]
    [InlineData("""{"Title":42}""")]
    [InlineData("""{"Title":["a"]}""")]
    [InlineData("""["Title"]""")]
    [InlineData("""{"Title":null}""")]
    [InlineData("""{"Title":"   "}""")]
    [InlineData("")]
    [InlineData("\"строка\"")]
    public void List_BadManifest_FallsBackToFileName(string manifest)
    {
        Add("Scripts/Отчёт.cmd");
        Add("Scripts/Отчёт.json", manifest);
        var script = Assert.Single(ScriptCatalog.List(Folder));
        Assert.Equal("Отчёт", script.Title);
    }

    [Fact]
    public void List_DescriptionNotString_Ignored()
    {
        Add("Scripts/a.cmd");
        Add("Scripts/a.json", """{"Title":"А","Description":{"x":1}}""");
        var script = Assert.Single(ScriptCatalog.List(Folder));
        Assert.Equal("А", script.Title);
        Assert.Null(script.Description);
    }

    [Fact]
    public void List_BatHidesPs1_ButExeAndPs1BothShown()
    {
        Add("Scripts/a.bat");
        Add("Scripts/a.ps1");
        Add("Scripts/b.exe");
        Add("Scripts/b.ps1");
        Assert.Equal(["a.bat", "b.exe", "b.ps1"], ScriptCatalog.List(Folder).Select(s => s.FileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void List_LauncherInOtherFolder_DoesNotHidePs1()
    {
        Add("Scripts/one/a.cmd");
        Add("Scripts/two/a.ps1");
        Assert.Equal(2, ScriptCatalog.List(Folder).Count);
    }

    [Fact]
    public void List_UppercaseExtensions_Recognized()
    {
        Add("Scripts/A.CMD");
        Add("Scripts/B.PS1");
        Add("Scripts/C.TXT");
        Assert.Equal(["A", "B"], ScriptCatalog.List(Folder).Select(s => s.Title));
    }

    [Fact]
    public void List_EmptyFolder_Empty()
    {
        Directory.CreateDirectory(Folder);
        Assert.Empty(ScriptCatalog.List(Folder));
    }

    [Fact]
    public void Seed_NoBundledFolder_DoesNothing()
    {
        ScriptCatalog.Seed(Folder, Path.Combine(_root, "нет"));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void Seed_Twice_Idempotent()
    {
        Add("bundled/a.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        ScriptCatalog.Seed(Folder, Bundled);
        Assert.Equal("1", File.ReadAllText(Path.Combine(Folder, "a.cmd")));
    }

    [Fact]
    public void Seed_NoLongerShipped_Unchanged_Removed_Changed_Kept()
    {
        Add("bundled/old.cmd", "старая");
        Add("bundled/mine.cmd", "моя");
        ScriptCatalog.Seed(Folder, Bundled);
        File.WriteAllText(Path.Combine(Folder, "mine.cmd"), "правка");
        File.Delete(Path.Combine(Bundled, "old.cmd"));
        File.Delete(Path.Combine(Bundled, "mine.cmd"));

        ScriptCatalog.Seed(Folder, Bundled);

        Assert.False(File.Exists(Path.Combine(Folder, "old.cmd")));
        Assert.Equal("правка", File.ReadAllText(Path.Combine(Folder, "mine.cmd")));
    }

    [Fact]
    public void Seed_UserFileWithSameName_NotInManifest_NotOverwritten()
    {
        Add("Scripts/.bundled.json", "{}"); // учёт уже есть (не первая версия)
        Add("Scripts/a.cmd", "своя утилита пользователя");
        Add("bundled/a.cmd", "поставляемая");

        ScriptCatalog.Seed(Folder, Bundled);

        Assert.Equal("своя утилита пользователя", File.ReadAllText(Path.Combine(Folder, "a.cmd")));
    }

    [Fact]
    public void Seed_CorruptManifest_TreatedAsEmpty()
    {
        Add("Scripts/.bundled.json", "{испорчен");
        Add("bundled/a.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        Assert.Equal("1", File.ReadAllText(Path.Combine(Folder, "a.cmd")));
    }

    [Fact]
    public void Seed_ReadOnlyInstalledCopy_StillUpdated()
    {
        Add("bundled/a.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        var target = Path.Combine(Folder, "a.cmd");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(Bundled, "a.cmd"), "2");

        ScriptCatalog.Seed(Folder, Bundled);

        Assert.Equal("2", File.ReadAllText(target));
    }

    [Fact]
    public void Seed_ManifestNamedFileInBundle_Ignored()
    {
        Add("bundled/.bundled.json", """{"чужое":"x"}""");
        Add("bundled/a.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        ScriptCatalog.Seed(Folder, Bundled);
        Assert.DoesNotContain("чужое", File.ReadAllText(Path.Combine(Folder, ".bundled.json")));
        Assert.True(File.Exists(Path.Combine(Folder, "a.cmd")));
    }

    [Fact]
    public void Seed_NestedFolders_Created()
    {
        Add("bundled/x/y/z.cmd", "1");
        ScriptCatalog.Seed(Folder, Bundled);
        Assert.True(File.Exists(Path.Combine(Folder, "x", "y", "z.cmd")));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}

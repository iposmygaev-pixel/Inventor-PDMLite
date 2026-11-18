using PdmLite.Core.IO;

namespace PdmLite.Core.Tests;

public sealed class ContainedPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pdm-audit-").FullName;
    public sealed class Settings { public string Value { get; set; } = "default"; }

    public static IEnumerable<object[]> UnsafePaths() => new[] {
        "../a.ipt", "a/../../b.ipt", @"..\a.ipt", @"a\..\..\b.ipt", "/tmp/a.ipt", @"C:\a.ipt", "C:a.ipt",
        @"\\server\share\a.ipt", @"\a.ipt", "a//b.ipt", "./a.ipt", "a/./b.ipt", "a/../b.ipt",
        "a.ipt:stream", "a\0.ipt", "a\n.ipt", "a?.ipt", "a*.ipt", "a|b.ipt", "a<b.ipt", "a>b.ipt", "a\"b.ipt",
        "a./b.ipt", "a /b.ipt", "a.ipt ", "a.ipt.", "a/", "", "  ", "CON.ipt", "nul.ipt", "COM1/a.ipt",
        "LPT9.ipt", "COM¹.ipt", "LPT².ipt", "CONIN$.ipt", "CONOUT$.ipt"
    }.Select(p => new object[] { p });

    [Theory, MemberData(nameof(UnsafePaths))]
    public void RelativePaths_RejectEscapesAliasesAndWindowsDevices(string relative) =>
        Assert.ThrowsAny<ArgumentException>(() => ContainedPath.Resolve(_root, relative));

    [Theory]
    [InlineData("Крепёж/Болт.ipt")]
    [InlineData(@"Крепёж\Болт.ipt")]
    [InlineData("_versions/123/Болт.ipt")]
    [InlineData("DIN 471/Кольцо 15x1.ipt")]
    [InlineData("CON_/a.ipt")]
    public void RelativePaths_KeepValidLibraryFilesInsideRoot(string relative)
    {
        var full = ContainedPath.Resolve(_root, relative);
        Assert.True(ContainedPath.Contains(_root, full));
        Assert.EndsWith(relative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar), full);
    }

    [Fact]
    public void Contains_SeparatesSiblingFolderAndSupportsFilesystemRoot()
    {
        Assert.False(ContainedPath.Contains(_root, _root));
        Assert.False(ContainedPath.Contains(_root, _root + "-other/a.ipt"));
        Assert.False(ContainedPath.Contains(_root, Path.Combine(_root, "..", "a.ipt")));
        Assert.True(ContainedPath.Contains(Path.GetPathRoot(_root)!, Path.Combine(_root, "a.ipt")));
    }
    public sealed class Node { public Node? Next { get; set; } }

    public void Dispose() => Directory.Delete(_root, true);
}

using PdmLite.Core.IO;
using PdmLite.Hosting;

namespace PdmLite.Core.Tests;

public sealed class ModuleConfigurationTests : IDisposable
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
    public sealed class Node { public Node? Next { get; set; } }

    [Theory]
    [InlineData("null")][InlineData("{\"Modules\":null}")][InlineData("{\"Modules\":[null]}")]
    [InlineData("{\"Modules\":[{}]}")][InlineData("{\"Modules\":[{\"Id\":\" \"}]}")]
    [InlineData("{\"Modules\":[{\"Id\":\"A\"},{\"Id\":\"a\"}]}")]
    public void Modules_InvalidShapeFallsBackWithDiagnostic(string json)
    {
        var file = Path.Combine(_root, "modules.json"); File.WriteAllText(file, json);
        var config = ModulesConfig.Load(file);
        Assert.NotNull(config.LoadError); Assert.True(config.IsEnabled("A"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

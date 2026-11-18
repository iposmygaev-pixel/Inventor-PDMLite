using System.Text.Json;
using PdmLite.Hosting;

namespace PdmLite.Core.Tests;

public sealed class JsonSettingsTests : IDisposable
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

    [Fact]
    public async Task Settings_ConcurrentInstancesDoNotShareTemporaryFile()
    {
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            var settings = new JsonSettings<Settings>("settings", _root);
            settings.Value.Value = new string((char)('a' + i), 1000); settings.Save();
        })));
        Assert.Equal(1000, new JsonSettings<Settings>("settings", _root).Value.Value.Length);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("null")][InlineData("[]")][InlineData("42")][InlineData("\"text\"")]
    [InlineData("")][InlineData("{")][InlineData("{\"Value\": []}")]
    public void Settings_DamagedContentUsesDefaults(string json)
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), json);
        Assert.Equal("default", new JsonSettings<Settings>("settings", _root).Value.Value);
    }

    [Theory]
    [InlineData("../escape")][InlineData(@"..\escape")][InlineData("/absolute")]
    [InlineData("a:stream")][InlineData(" ")][InlineData("")][InlineData("CON")]
    public void Settings_InvalidNamesDoNotWriteOutsideFolder(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => new JsonSettings<Settings>(name, _root));

    [Fact]
    public void Settings_SerializationFailurePreservesLastSavedFile()
    {
        var valid = new JsonSettings<Settings>("settings", _root); valid.Save();
        var original = File.ReadAllText(valid.FilePath);
        var cyclic = new JsonSettings<Node>("settings", _root); cyclic.Value.Next = cyclic.Value;
        Assert.Throws<JsonException>(() => cyclic.Save());
        Assert.Equal(original, File.ReadAllText(valid.FilePath)); Assert.Single(Directory.GetFiles(_root));
    }
    public sealed class Node { public Node? Next { get; set; } }

    public void Dispose() => Directory.Delete(_root, true);
}

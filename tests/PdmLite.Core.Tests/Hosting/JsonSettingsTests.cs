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
    [InlineData("null")][InlineData("{")][InlineData("{\"Value\": []}")]
    public void Settings_DamagedContentIsKeptBesideAndReported(string json)
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, json);
        var logger = new ListLogger();
        var settings = new JsonSettings<Settings>("settings", _root, logger);

        Assert.NotNull(settings.DamagedCopyPath);
        Assert.Equal(_root, Path.GetDirectoryName(settings.DamagedCopyPath));
        Assert.StartsWith("settings.json.damaged-", Path.GetFileName(settings.DamagedCopyPath));
        Assert.Equal(json, File.ReadAllText(settings.DamagedCopyPath!));
        Assert.Contains(settings.DamagedCopyPath!, settings.LoadProblem);
        Assert.Contains(logger.Messages, m => m.Contains("повреждён") && m.Contains(settings.DamagedCopyPath!));

        // Сохранение после повреждения пишет новые настройки, копия прежнего файла остаётся.
        settings.Value.Value = "new";
        settings.Save();
        Assert.Equal(json, File.ReadAllText(settings.DamagedCopyPath!));
        Assert.Equal("new", new JsonSettings<Settings>("settings", _root).Value.Value);
    }

    [Fact]
    public void Settings_RepeatedLoadOfSameDamagedFileKeepsOneCopy()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{");
        var first = new JsonSettings<Settings>("settings", _root);
        first.Reload();
        var second = new JsonSettings<Settings>("settings", _root);
        Assert.Equal(first.DamagedCopyPath, second.DamagedCopyPath);
        Assert.Single(Directory.GetFiles(_root, "settings.json.damaged-*.bak"));

        File.WriteAllText(Path.Combine(_root, "settings.json"), "[");
        var third = new JsonSettings<Settings>("settings", _root);
        Assert.NotEqual(first.DamagedCopyPath, third.DamagedCopyPath);
        Assert.Equal(2, Directory.GetFiles(_root, "settings.json.damaged-*.bak").Length);
    }

    [Fact]
    public void Settings_ValidOrMissingFileHasNoProblemAndNoCopy()
    {
        var missing = new JsonSettings<Settings>("settings", _root);
        Assert.Null(missing.LoadProblem);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{\"Value\": \"saved\"}");
        var valid = new JsonSettings<Settings>("settings", _root);
        Assert.Equal("saved", valid.Value.Value);
        Assert.Null(valid.LoadProblem);
        Assert.Null(valid.DamagedCopyPath);
        Assert.Single(Directory.GetFiles(_root));
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
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

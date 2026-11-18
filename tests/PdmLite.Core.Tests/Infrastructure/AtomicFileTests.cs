using System.Text.Json;
using PdmLite.Core.IO;

namespace PdmLite.Core.Tests;

public sealed class AtomicFileTests : IDisposable
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
    public async Task AtomicWrites_ConcurrentWritersProduceOneCompleteValueAndNoTemporaryFiles()
    {
        var file = Path.Combine(_root, "data.json");
        var contents = Enumerable.Range(0, 32).Select(i => JsonSerializer.Serialize(new { id = i, text = new string((char)('a' + i % 26), 20000) })).ToArray();
        await Task.WhenAll(contents.Select(text => Task.Run(() => AtomicFile.WriteAllText(file, text))));
        Assert.Contains(File.ReadAllText(file), contents);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AtomicWrite_CanceledWriteKeepsOldContent()
    {
        var file = Path.Combine(_root, "data"); File.WriteAllText(file, "old");
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAllTextAsync(file, "new", ct.Token));
        Assert.Equal("old", File.ReadAllText(file)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void AtomicWrite_RenameFailureCleansOnlyItsOwnTemporaryFile()
    {
        var target = Path.Combine(_root, "directory"); Directory.CreateDirectory(target);
        var unrelated = target + ".tmp"; File.WriteAllText(unrelated, "keep");
        var failure = Record.Exception(() => AtomicFile.WriteAllText(target, "data"));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.True(Directory.Exists(target));
        Assert.Equal("keep", File.ReadAllText(unrelated)); Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicWrite_OpenReaderAllowsReplacementAndKeepsOldSnapshot(bool asynchronous)
    {
        var file = Path.Combine(_root, "snapshot.txt");
        AtomicFile.WriteAllText(file, "old snapshot");
        using var reader = new StreamReader(AtomicFile.OpenRead(file));
        if (asynchronous) await AtomicFile.WriteAllTextAsync(file, "new snapshot");
        else AtomicFile.WriteAllText(file, "new snapshot");
        Assert.Equal("old snapshot", await reader.ReadToEndAsync());
        Assert.Equal("new snapshot", File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AtomicWrites_MixedSyncAndAsyncWritersShareLockForNormalizedPath()
    {
        var file = Path.Combine(_root, "mixed.txt");
        var alias = Path.Combine(_root, "unused", "..", "mixed.txt");
        var contents = Enumerable.Range(0, 40).Select(i => $"{i}:" + new string((char)('a' + i % 26), 20000)).ToArray();
        await Task.WhenAll(contents.Select((text, i) => i % 2 == 0
            ? Task.Run(() => AtomicFile.WriteAllText(file, text))
            : AtomicFile.WriteAllTextAsync(alias, text)));
        Assert.Contains(File.ReadAllText(file), contents);
        Assert.Single(Directory.GetFiles(_root));
    }
    public sealed class Node { public Node? Next { get; set; } }

    public void Dispose() => Directory.Delete(_root, true);
}

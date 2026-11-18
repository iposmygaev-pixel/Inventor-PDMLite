using System.IO.Pipes;
using System.Text.Json;
using PdmLite.Core.Documents;
using PdmLite.Core.Scripts;

namespace PdmLite.Core.Tests;

/// <summary>Обмен с процессом свойств — сбои канала и данных.</summary>
public class PropertyHostEdgeTests
{
    private sealed class Store : IDocumentPropertyStore
    {
        public List<string> Calls { get; } = [];
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public Task<DocumentProperties> ReadAsync(string path, CancellationToken ct = default)
        {
            Calls.Add("read " + path);
            return Task.FromResult(new DocumentProperties { PartNumber = path });
        }

        public Task WriteAsync(string path, DocumentPropertyChanges changes, CancellationToken ct = default)
        {
            Calls.Add("write " + path + " " + changes.PartNumber);
            return Task.CompletedTask;
        }

        public Task ReplaceReferencesAsync(string path, IReadOnlyDictionary<string, string> map, CancellationToken ct = default)
        {
            Calls.Add($"replace {path} {map.Count} {map.ContainsKey("A.IPT")}");
            return Task.CompletedTask;
        }
    }

    private static async Task<(List<PropertyHostResponse> Responses, Store Store)> Serve(params string[] lines)
    {
        var store = new Store();
        var output = new StringWriter();
        await PropertyHostProtocol.ServeAsync(new StringReader(string.Join("\n", lines)), output, store);
        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonSerializer.Deserialize<PropertyHostResponse>(l, PropertyHostProtocol.Json)!).ToList();
        return (responses, store);
    }

    private static string Request(PropertyHostRequest request) => JsonSerializer.Serialize(request, PropertyHostProtocol.Json);

    [Fact]
    public async Task Serve_EmptyLines_Skipped()
    {
        var (responses, _) = await Serve("", Request(new("read", "a")), "", "");
        Assert.True(Assert.Single(responses).Ok);
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    public async Task Serve_BrokenRequest_AnswersError_AndContinues(string broken)
    {
        var (responses, store) = await Serve(broken, Request(new("read", "после")));
        Assert.Equal(2, responses.Count);
        Assert.False(responses[0].Ok);
        Assert.False(string.IsNullOrEmpty(responses[0].Error));
        Assert.True(responses[1].Ok);
        Assert.Equal(["read после"], store.Calls);
    }

    [Fact]
    public async Task Serve_ReadNoGeometry_FallsBackToDefaultInterfaceMethod()
    {
        var (responses, store) = await Serve(Request(new("readNoGeometry", "x")));
        Assert.Equal("x", responses[0].Properties!.PartNumber);
        Assert.Equal(["read x"], store.Calls); // хранилище без отдельного чтения — обычное чтение
    }

    [Fact]
    public async Task Serve_WriteWithoutChanges_And_ReplaceWithoutMap_AreAccepted()
    {
        var (responses, store) = await Serve("""{"Op":"write","Path":"a"}""", """{"Op":"replace","Path":"b"}""");
        Assert.All(responses, r => Assert.True(r.Ok));
        Assert.Equal(["write a ", "replace b 0 False"], store.Calls);
    }

    [Fact]
    public async Task Serve_ReplaceMap_IsCaseInsensitive()
    {
        var (_, store) = await Serve(Request(new("replace", "s.iam", Map: new() { ["a.ipt"] = "b.ipt" })));
        Assert.Equal(["replace s.iam 1 True"], store.Calls);
    }

    [Fact]
    public async Task Serve_Cyrillic_NotEscaped()
    {
        var output = new StringWriter();
        await PropertyHostProtocol.ServeAsync(new StringReader(Request(new("read", "Болт"))), output, new Store());
        Assert.Contains("Болт", output.ToString());
    }

    [Fact]
    public async Task Client_ServerClosed_IOException()
    {
        var client = new PropertyHostClient(new StringReader(""), new StringWriter());
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Client_ErrorResponse_WithoutText_HasDefaultMessage()
    {
        var client = new PropertyHostClient(new StringReader("""{"Ok":false}""" + "\n"), new StringWriter());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromSeconds(5)));
        Assert.Contains("Ошибка процесса свойств", error.Message);
        Assert.False(client.IsBroken); // ответ получен — порядок не сбит
    }

    [Fact]
    public async Task Client_NullResponse_InvalidData()
    {
        var client = new PropertyHostClient(new StringReader("null\n"), new StringWriter());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Client_WritesOneLinePerRequest()
    {
        var output = new StringWriter();
        var client = new PropertyHostClient(new StringReader("{\"Ok\":true}\n{\"Ok\":true}\n"), output);
        await client.SendAsync(new("read", "a\nb"), TimeSpan.FromSeconds(5)); // перевод строки в пути не рвёт протокол
        await client.SendAsync(new("read", "c"), TimeSpan.FromSeconds(5));
        Assert.Equal(2, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static (PropertyHostClient Client, StreamWriter Responses) SilentServer()
    {
        var responses = new AnonymousPipeServerStream(PipeDirection.Out);
        var responsesIn = new AnonymousPipeClientStream(PipeDirection.In, responses.ClientSafePipeHandle);
        return (new PropertyHostClient(new StreamReader(responsesIn), new StringWriter()), new StreamWriter(responses) { AutoFlush = true });
    }

    [Fact]
    public async Task Client_Timeout_TimeoutException_ThenBroken()
    {
        var (client, responses) = SilentServer();
        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromMilliseconds(100)));
        Assert.True(client.IsBroken);

        // Опоздавший ответ на первый запрос не должен стать ответом на второй.
        await responses.WriteLineAsync("""{"Ok":true,"Properties":{"PartNumber":"a"}}""");
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(new("read", "b"), TimeSpan.FromSeconds(5)));
        responses.Dispose();
    }

    [Fact]
    public async Task Client_Cancelled_OperationCanceled_ThenBroken()
    {
        var (client, responses) = SilentServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromMinutes(1), cts.Token));
        Assert.True(client.IsBroken);
        responses.Dispose();
    }

    [Fact]
    public async Task Client_CancelledBeforeSend_NotBroken()
    {
        var client = new PropertyHostClient(new StringReader(""), new StringWriter());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(new("read", "a"), TimeSpan.FromMinutes(1), cts.Token));
        Assert.False(client.IsBroken); // запрос не ушёл — порядок цел
    }

    [Fact]
    public async Task UnavailableStore_Throws_WithReason()
    {
        var store = new UnavailablePropertyStore("нет Inventor");
        Assert.False(store.IsAvailable);
        Assert.Equal("нет Inventor", store.UnavailableReason);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.ReadAsync("a"));
        Assert.Contains("нет Inventor", error.Message);
    }
}

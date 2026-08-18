using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MyTranslator.Api.Data;
using MyTranslator.Api.Translation;
using OllamaSharp;

namespace MyTranslator.Api.Tests;

public sealed class AiChatClientFactoryTests
{
    [Fact]
    public async Task OllamaConnectorReturnsChatResponseThroughSharedInterface()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                model = "qwen-test",
                created_at = DateTimeOffset.UtcNow,
                message = new { role = "assistant", content = "translated" },
                done = true,
                done_reason = "stop"
            })
        });
        using var httpClient = new HttpClient(handler);
        var chatClient = CreateOllamaChatClient(httpClient);
        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")]);

        Assert.IsAssignableFrom<OllamaApiClient>(chatClient);
        Assert.Equal("translated", response.Text);
        Assert.Equal(new Uri("http://ollama.test/api/chat"), handler.RequestUri);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("qwen-test", request.RootElement.GetProperty("model").GetString());
        Assert.False(request.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task OllamaBadRequestKeepsStatusForExistingErrorMapping()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { error = "invalid request" })
        });
        using var httpClient = new HttpClient(handler);
        var chatClient = CreateOllamaChatClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")]));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task ConfiguredOllamaProviderTranslatesAndPersistsSegment()
    {
        var handler = new OllamaTranslationHandler();
        using var factory = new ApiFactory(
            "Development",
            null,
            translationHandler: handler,
            seedTranslationConfiguration: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "sk-dev-00000000000000000000000000000000");
        var providerResponse = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "Local Ollama",
            kind = "ollama",
            baseUrl = "http://ollama.test",
            enabled = true,
            isDefault = true
        });
        var provider = await providerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Created, providerResponse.StatusCode);
        var providerId = provider.GetProperty("id").GetGuid();
        var modelResponse = await client.PostAsJsonAsync($"/api/providers/{providerId}/models", new
        {
            modelId = "qwen-test",
            displayName = "Qwen Test",
            isDefault = true
        });
        Assert.Equal(HttpStatusCode.Created, modelResponse.StatusCode);

        var taskId = await ImportTextAsync(client, "Hello");
        var createRunResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var createdRun = await createRunResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Accepted, createRunResponse.StatusCode);
        var run = await WaitForTerminalRunAsync(client, taskId, createdRun.GetProperty("runId").GetGuid());
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(segments.GetProperty("items").EnumerateArray());

        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal("你好", segment.GetProperty("targetText").GetString());
        Assert.Equal(new Uri("http://ollama.test/api/chat"), handler.RequestUri);
    }

    private static IChatClient CreateOllamaChatClient(HttpClient httpClient)
    {
        var factory = new AiChatClientFactory(new StubHttpClientFactory(httpClient));
        return factory.Create(
            new AiProvider { Kind = "ollama", BaseUrl = "http://ollama.test" },
            new AiModel { ModelId = "qwen-test" });
    }

    private static async Task<Guid> ImportTextAsync(HttpClient client, string text)
    {
        using var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        request.Add(file, "file", "sample.txt");
        request.Add(new StringContent("txt"), "fileType");
        var response = await client.PostAsync("/api/tasks/imports/file", request);
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return task.GetProperty("taskId").GetGuid();
    }

    private static async Task<JsonElement> WaitForTerminalRunAsync(HttpClient client, Guid taskId, Guid runId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>(
                $"/api/tasks/{taskId}/translation-runs/{runId}");
            if (run.GetProperty("status").GetString() is "completed" or "partial_failed" or "failed")
            {
                return run;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The translation run did not reach a terminal state.");
    }

    private sealed class StubHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class OllamaTranslationHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
            var messages = body.RootElement.GetProperty("messages");
            var userMessage = messages[1];
            var userContent = userMessage.GetProperty("content").GetString()!;
            using var input = JsonDocument.Parse(userContent);
            var segments = input.RootElement.GetProperty("segments");
            var segmentId = segments[0].GetProperty("segmentId").GetGuid();
            var content = JsonSerializer.Serialize(new
            {
                translations = new[] { new { segmentId, targetText = "你好" } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    model = "qwen-test",
                    created_at = DateTimeOffset.UtcNow,
                    message = new { role = "assistant", content },
                    done = true,
                    done_reason = "stop"
                })
            };
        }
    }
}

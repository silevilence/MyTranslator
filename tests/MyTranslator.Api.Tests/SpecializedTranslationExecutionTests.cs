using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Tests;

public sealed class SpecializedTranslationExecutionTests
{
    [Theory]
    [InlineData("hy-mt2", "openai", false)]
    [InlineData("hy-mt2", "openai", true)]
    [InlineData("hy-mt2", "ollama", false)]
    [InlineData("hy-mt2", "ollama", true)]
    [InlineData("bilibili-index-translate", "openai", false)]
    [InlineData("bilibili-index-translate", "openai", true)]
    [InlineData("bilibili-index-translate", "ollama", false)]
    [InlineData("bilibili-index-translate", "ollama", true)]
    public async Task ConnectorsSendSingleUserAndRetryOnlyCurrentSegment(string format, string kind, bool terms)
    {
        var handler = new CaptureHandler(kind, (call, prompt) => call == 1 ? "" : prompt.Contains("Hello") ? "你好\n世界" : "再见");
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format, kind);
        if (terms) await AddTerm(client, "hello", "你好");
        var taskId = await SpecializedTestApi.Import(client, "Hello\nworld\n\nGoodbye");
        var run = await Run(client, taskId);
        Assert.Equal("completed", run.GetProperty("status").GetString());
        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(requests[0].GetProperty("messages").GetRawText(), requests[1].GetProperty("messages").GetRawText());
        foreach (var request in requests)
        {
            var message = Assert.Single(request.GetProperty("messages").EnumerateArray());
            Assert.Equal("user", message.GetProperty("role").GetString());
            Assert.Contains("Simplified Chinese", message.GetProperty("content").GetString());
            Assert.False(request.TryGetProperty("response_format", out _));
            Assert.False(request.TryGetProperty("format", out _));
            Assert.False(request.TryGetProperty("tools", out _));
            if (format == "bilibili-index-translate")
            {
                if (kind == "openai")
                {
                    Assert.False(request.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
                    Assert.False(request.TryGetProperty("reasoning_effort", out _));
                }
                else Assert.False(request.GetProperty("think").GetBoolean());
            }
        }
        var firstPrompt = requests[0].GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("Hello\nworld", firstPrompt);
        Assert.Equal(terms, firstPrompt.Contains("hello"));
        Assert.DoesNotContain("hello", requests[2].GetProperty("messages")[0].GetProperty("content").GetString());
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        Assert.Equal("你好\n世界", segments.GetProperty("items")[0].GetProperty("targetText").GetString());
        Assert.Equal("再见", segments.GetProperty("items")[1].GetProperty("targetText").GetString());
    }

    [Theory]
    [InlineData("hy-mt2", "", "stop", "llm_response_invalid")]
    [InlineData("hy-mt2", "译文", "length", "llm_output_truncated")]
    [InlineData("hy-mt2", "参考下面的翻译：Hello 翻译成 你好", "stop", "llm_prompt_echo")]
    [InlineData("hy-mt2", "Hello 翻译成 你好", "stop", "llm_prompt_echo")]
    [InlineData("bilibili-index-translate", "【约束要求】只输出译文", "stop", "llm_prompt_echo")]
    [InlineData("bilibili-index-translate", "你好→你好", "stop", "llm_prompt_echo")]
    [InlineData("bilibili-index-translate", "<x999/>你好", "stop", "placeholder_integrity_violation")]
    public async Task InvalidOutputsDoNotPolluteSuccessfulSegments(string format, string output, string finish, string code)
    {
        var handler = new CaptureHandler("openai", (_, prompt) => prompt.Contains("Hello") ? output : "再见",
            prompt => prompt.Contains("Hello") ? finish : "stop");
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format);
        await AddTerm(client, "Hello", "你好");
        var taskId = await SpecializedTestApi.Import(client, "Hello\n\nGoodbye");
        var run = await Run(client, taskId);
        Assert.Equal("partial_failed", run.GetProperty("status").GetString());
        var failures = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/translation-runs/{run.GetProperty("runId").GetGuid()}/failures");
        Assert.Equal(code, Assert.Single(failures.GetProperty("items").EnumerateArray()).GetProperty("code").GetString());
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        Assert.Equal(JsonValueKind.Null, segments.GetProperty("items")[0].GetProperty("targetText").ValueKind);
        Assert.Equal("再见", segments.GetProperty("items")[1].GetProperty("targetText").GetString());
    }

    [Theory]
    [InlineData("hy-mt2")]
    [InlineData("bilibili-index-translate")]
    public async Task FormatSnapshotSurvivesEditsAndBudgetFailsBeforeHttp(string format)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CaptureHandler("openai", (_, _) => "译文")
        {
            BeforeResponse = async call => { if (call == 1) { entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15)); } }
        };
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        var (providerId, modelId) = await SpecializedTestApi.Configure(client, format);
        var taskId = await SpecializedTestApi.Import(client, "Hello\n\nGoodbye\n\n" + new string('长', 6000));
        var response = await client.PostAsJsonAsync($"/api/tasks/{taskId}/translation-runs", new
        { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN", providerId, modelId });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var update = await client.PutAsJsonAsync($"/api/providers/{providerId}/models/{modelId}", new
            { modelId = "test", displayName = "Test", translationMessageFormat = "general", isDefault = true });
            update.EnsureSuccessStatusCode();
        }
        finally { release.TrySetResult(); }
        var run = await SpecializedTestApi.Wait(client, taskId, created.GetProperty("runId").GetGuid());
        Assert.Equal(format, run.GetProperty("translationMessageFormat").GetString());
        Assert.Equal("partial_failed", run.GetProperty("status").GetString());
        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests) Assert.Single(request.GetProperty("messages").EnumerateArray());
        var failures = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/translation-runs/{created.GetProperty("runId").GetGuid()}/failures");
        var failure = Assert.Single(failures.GetProperty("items").EnumerateArray());
        Assert.Equal("translation_request_too_large", failure.GetProperty("code").GetString());
        Assert.Equal(0, failure.GetProperty("attempts").GetInt32());
        Assert.False(failure.GetProperty("retryable").GetBoolean());
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("ollama")]
    public async Task IndexParameterRejectionIsNotRetried(string kind)
    {
        var handler = new CaptureHandler(kind, (_, _) => "") { Status = HttpStatusCode.BadRequest };
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, "bilibili-index-translate", kind);
        var taskId = await SpecializedTestApi.Import(client, "Hello");
        var run = await Run(client, taskId);
        Assert.Equal("llm_request_rejected", run.GetProperty("failure").GetProperty("code").GetString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AutoSourceSkipsTermsAndUnknownLanguageAndThinkingConflictAreRejected()
    {
        var handler = new CaptureHandler("openai", (_, _) => "你好");
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, "bilibili-index-translate");
        await AddTerm(client, "Hello", "你好");
        var taskId = await SpecializedTestApi.Import(client, "Hello");
        foreach (var (target, effort, code) in new[] { ("zz", (string?)null, "unsupported_language_pair"), ("zh-CN", "high", "translation_reasoning_conflict") })
        {
            var response = await client.PostAsJsonAsync($"/api/tasks/{taskId}/translation-runs", new
            { extractionRevision = 1, targetLanguage = target, reasoningEffort = effort });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        await Run(client, taskId, null, "none");
        Assert.DoesNotContain("专名/术语对照:", handler.Requests.Single().GetProperty("messages")[0].GetProperty("content").GetString());
    }

    internal static async Task AddTerm(HttpClient client, string source, string target, string language = "en", bool caseSensitive = false)
    {
        var response = await client.PostAsJsonAsync("/api/terms", new
        { sourceTerm = source, targetTerm = target, sourceLanguage = language, targetLanguage = "zh-CN", caseSensitive });
        response.EnsureSuccessStatusCode();
    }

    internal static async Task<JsonElement> Run(HttpClient client, Guid taskId, string? sourceLanguage = "en", string? effort = null)
    {
        var response = await client.PostAsJsonAsync($"/api/tasks/{taskId}/translation-runs", new
        { extractionRevision = 1, sourceLanguage, targetLanguage = "zh-CN", reasoningEffort = effort });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return await SpecializedTestApi.Wait(client, taskId, created.GetProperty("runId").GetGuid());
    }

    internal sealed class CaptureHandler(string kind, Func<int, string, string> output, Func<string, string>? finish = null) : HttpMessageHandler
    {
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        public Func<int, Task>? BeforeResponse { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Enqueue(body);
            var call = Requests.Count;
            var prompt = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
            if (BeforeResponse is not null) await BeforeResponse(call);
            if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status) { Content = JsonContent.Create(new { error = "unsupported parameters" }) };
            var text = output(call, prompt);
            var reason = finish?.Invoke(prompt) ?? "stop";
            return new HttpResponseMessage(Status)
            {
                Content = kind == "openai" ? JsonContent.Create(new
                {
                    id = "test", @object = "chat.completion", created = 1, model = "test",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = reason } }
                }) : JsonContent.Create(new
                {
                    model = "test", created_at = DateTimeOffset.UtcNow,
                    message = new { role = "assistant", content = text }, done = true, done_reason = reason
                })
            };
        }
    }
}

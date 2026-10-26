using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Tests;

public sealed class SubtitleApiTests
{
    private const string Srt = "7\r\n00:00:01,000 --> 00:00:04,000\r\n<b>Hello</b> &amp; world\r\nSecond line\r\n\r\n9\r\n00:00:02,500 --> 00:00:05,000\r\nOverlap\r\n\r\n11\r\n00:00:07,000 --> 00:00:08,000\r\n\r\n15\r\n00:00:09,000 --> 00:00:10,000\r\n<font color=\"#ff0000\">Last</font>";

    [Theory]
    [InlineData("utf8", false)]
    [InlineData("utf8", true)]
    [InlineData("utf16", true)]
    [InlineData("utf16be", true)]
    public async Task SrtRoundTripPreservesOriginalBytes(string encoding, bool bom)
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        Encoding codec = encoding switch { "utf16" => Encoding.Unicode, "utf16be" => Encoding.BigEndianUnicode, _ => Encoding.UTF8 };
        var bytes = (bom ? codec.GetPreamble() : []).Concat(codec.GetBytes(Srt)).ToArray();
        var task = await Import(client, bytes, "sample.srt");
        Assert.Equal("srt", task.GetProperty("fileType").GetString());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("segmentation").ValueKind);
        Assert.False(task.GetProperty("capabilities").GetProperty("canReextract").GetBoolean());
        var id = task.GetProperty("taskId").GetGuid();
        var segments = await Segments(client, id);
        Assert.Equal(3, segments.Length);
        Assert.Contains("\r\nSecond line", segments[0].GetProperty("sourceText").GetString());
        Assert.DoesNotContain("00:00:", segments[0].GetProperty("sourceText").GetString());
        foreach (var segment in segments)
            (await Save(client, id, segment, segment.GetProperty("sourceText").GetString()!)).EnsureSuccessStatusCode();
        var exported = await client.GetAsync($"/api/tasks/{id}/result");
        exported.EnsureSuccessStatusCode();
        Assert.Equal("application/x-subrip", exported.Content.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, await exported.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("1\n00:60:01,000 --> 00:00:02,000\nHello")]
    [InlineData("1\n00:00:02,000 --> 00:00:01,000\nHello")]
    [InlineData("1\n00:00:01,000 --> 00:00:01,000\nHello")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n<b><i>Hello</b></i>")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n<unknown>Hello</unknown>")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n{\\an8}Hello")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nA &bad; B")]
    public async Task InvalidSrtCreatesNoTask(string source)
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        using var form = Form(Encoding.UTF8.GetBytes(source), "invalid.srt");
        await Problem(await client.PostAsync("/api/tasks/imports/file", form), "subtitle_parse_failed");
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().TranslationTasks.CountAsync());
    }

    [Theory]
    [InlineData("bad\n\nextra")]
    [InlineData("bad\n \nextra")]
    [InlineData("bad --> extra")]
    [InlineData("<i>new tag</i>")]
    [InlineData("bad\u0001")]
    [InlineData("bad &amp; extra")]
    [InlineData("bad\n")]
    public async Task IllegalBodyIsRejectedBySaveAndExport(string target)
    {
        using var factory = new ApiFactory();
        using var client = Client(factory);
        var task = await Import(client, Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHello"), "a.srt");
        var id = task.GetProperty("taskId").GetGuid();
        var segment = Assert.Single(await Segments(client, id));
        await Problem(await Save(client, id, segment, target), "subtitle_structure_violation");
        var unchanged = Assert.Single(await Segments(client, id));
        Assert.Equal(1, unchanged.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, unchanged.GetProperty("targetText").ValueKind);
        // 模拟旧数据或绕过 API 的写入，导出仍须独立保护。
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.TranslationSegments.SingleAsync()).TargetText = target;
            await db.SaveChangesAsync();
        }
        await Problem(await client.GetAsync($"/api/tasks/{id}/result"), "subtitle_structure_violation");
    }

    [Fact]
    public async Task SrtUsesExistingTranslationEditingConfirmationReviewPipeline()
    {
        using var factory = new ApiFactory("Development", null, translationProvider: new SubtitleChatClient());
        using var client = Client(factory);
        var task = await Import(client, Encoding.UTF8.GetBytes(Srt), "wrong.txt", "srt");
        var id = task.GetProperty("taskId").GetGuid();
        Assert.Equal("completed", (await Run(client, id, "translation")).GetProperty("status").GetString());
        var segments = await Segments(client, id);
        var changed = segments[0].GetProperty("sourceText").GetString()!.Replace("Hello", "你好").Replace("Second line", "第二行\n第三行");
        (await Save(client, id, segments[0], changed, "confirmed")).EnsureSuccessStatusCode();
        Assert.Equal("completed", (await Run(client, id, "review")).GetProperty("status").GetString());
        var refreshed = await Segments(client, id);
        Assert.Equal("confirmed", refreshed[0].GetProperty("confirmationStatus").GetString());
        Assert.Single(refreshed[0].GetProperty("reviewComments").EnumerateArray());
        var result = await client.GetAsync($"/api/tasks/{id}/result");
        result.EnsureSuccessStatusCode();
        Assert.EndsWith(".srt", result.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(Srt.Replace("Hello", "你好").Replace("Second line", "第二行\r\n第三行"), await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidAiSubtitlePreservesSuccessfulSegmentsAndCanRecover()
    {
        using var factory = new ApiFactory("Development", null, translationProvider: new SubtitleChatClient(true));
        using var client = Client(factory);
        var id = (await Import(client, Encoding.UTF8.GetBytes(Srt), "a.srt")).GetProperty("taskId").GetGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Providers.SingleAsync()).MaxAttempts = 1;
            await db.SaveChangesAsync();
        }
        var run = await Run(client, id, "translation");
        Assert.Equal("partial_failed", run.GetProperty("status").GetString());
        var segments = await Segments(client, id);
        Assert.Equal(JsonValueKind.Null, segments[1].GetProperty("targetText").ValueKind);
        Assert.Equal(JsonValueKind.String, segments[0].GetProperty("targetText").ValueKind);
        var failures = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}/translation-runs/{run.GetProperty("runId").GetGuid()}/failures");
        Assert.Contains("subtitle_structure_violation", failures.ToString());
        (await Save(client, id, segments[1], "重叠字幕")).EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/tasks/{id}/result")).EnsureSuccessStatusCode();
    }

    private static HttpClient Client(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-dev-00000000000000000000000000000000");
        return client;
    }

    private static MultipartFormDataContent Form(byte[] bytes, string name, string? type = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name);
        if (type is not null) form.Add(new StringContent(type), "fileType");
        return form;
    }

    private static async Task<JsonElement> Import(HttpClient client, byte[] bytes, string name, string? type = null)
    {
        using var form = Form(bytes, name, type);
        var response = await client.PostAsync("/api/tasks/imports/file", form);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement[]> Segments(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}/segments")).GetProperty("items").EnumerateArray().ToArray();

    private static Task<HttpResponseMessage> Save(HttpClient client, Guid id, JsonElement segment, string text, string status = "translated") =>
        client.PutAsJsonAsync($"/api/tasks/{id}/segments/{segment.GetProperty("id").GetGuid()}", new
        {
            extractionRevision = 1, version = segment.GetProperty("version").GetInt32(), targetText = text,
            confirmationStatus = status, sourceLanguage = "en", targetLanguage = "zh-CN"
        });

    private static async Task Problem(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static async Task<JsonElement> Run(HttpClient client, Guid id, string kind)
    {
        var created = await client.PostAsJsonAsync($"/api/tasks/{id}/{kind}-runs", new
        { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        created.EnsureSuccessStatusCode();
        for (var i = 0; i < 200; i++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>(created.Headers.Location);
            if (run.GetProperty("status").GetString() is "completed" or "partial_failed" or "failed") return run;
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    private sealed class SubtitleChatClient(bool invalid = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var request = JsonDocument.Parse(messages.Last(message => message.Role == ChatRole.User).Text);
            var segments = request.RootElement.GetProperty("segments").EnumerateArray().ToArray();
            var review = segments[0].TryGetProperty("targetText", out _);
            var content = review ? JsonSerializer.Serialize(new
            {
                reviews = segments.Select(segment => new { segmentId = segment.GetProperty("segmentId").GetGuid(),
                    comments = new[] { new { severity = "low", issue = "检查完成", suggestion = (string?)null } } })
            }) : JsonSerializer.Serialize(new
            {
                translations = segments.Select(segment => new { segmentId = segment.GetProperty("segmentId").GetGuid(),
                    targetText = invalid && segment.GetProperty("sourceText").GetString() == "Overlap" ? "bad\n\nbody" : segment.GetProperty("sourceText").GetString() })
            });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, content)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

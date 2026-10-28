using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;
using MyTranslator.Api.Review;
using MyTranslator.Api.TaskOperations;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.Tests;

public sealed class TaskReferenceTests
{
    [Fact]
    public async Task GlossariesIsolateDuplicatesPaginationAlignmentAndDeletion()
    {
        using var factory = new ApiFactory();
        using var client = SpecializedTestApi.Client(factory);
        var first = await GlossaryAsync(client, "游戏");
        var second = await GlossaryAsync(client, "小说");
        await TermAsync(client, first, "Apple", "苹果公司");
        await TermAsync(client, first, "Pear", "梨");
        await TermAsync(client, second, "Apple", "苹果");
        await Error(await client.PostAsJsonAsync("/api/terms", new
        { glossaryId = first, sourceTerm = "apple", targetTerm = "其他", sourceLanguage = "en", targetLanguage = "zh-CN" }), "term_conflict", 409);
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/terms?glossaryId={first}&limit=1");
        Assert.Equal(first, page.GetProperty("items")[0].GetProperty("glossaryId").GetGuid());
        var cursor = Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!);
        await Error(await client.GetAsync($"/api/terms?glossaryId={second}&cursor={cursor}"), "invalid_cursor", 400);
        var task = await SpecializedTestApi.Import(client, "Apple");
        await SetReference(client, task, second, "水果故事");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.TranslationSegments.SingleAsync()).TargetText = "苹果";
            await db.SaveChangesAsync();
        }
        var alignment = await (await client.PostAsJsonAsync($"/api/tasks/{task}/term-alignment-checks",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(alignment.GetProperty("items").EnumerateArray());
        Assert.Equal(1, alignment.GetProperty("summary").GetProperty("languagePairTermCount").GetInt32());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/api/terms?glossaryId={second}")).GetProperty("items").EnumerateArray());
        await Error(await client.DeleteAsync($"/api/glossaries/{second}?version=1"), "glossary_in_use", 409);
        var unused = await GlossaryAsync(client, "临时");
        (await client.PutAsJsonAsync($"/api/glossaries/{unused}", new { name = "改名", version = 1 })).EnsureSuccessStatusCode();
        await Error(await client.DeleteAsync($"/api/glossaries/{unused}?version=1"), "glossary_version_conflict", 409);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/glossaries/{unused}?version=2")).StatusCode);
        await Error(await client.DeleteAsync($"/api/glossaries/{Glossary.DefaultId}?version=1"), "glossary_in_use", 409);
        var glossaryPage = await client.GetFromJsonAsync<JsonElement>("/api/glossaries?limit=1");
        Assert.NotNull(glossaryPage.GetProperty("nextCursor").GetString());
        var next = await client.GetFromJsonAsync<JsonElement>("/api/glossaries?limit=1&cursor=" + Uri.EscapeDataString(glossaryPage.GetProperty("nextCursor").GetString()!));
        Assert.NotEqual(glossaryPage.GetProperty("items")[0].GetProperty("id"), next.GetProperty("items")[0].GetProperty("id"));
    }

    [Fact]
    public async Task ReferenceValidationVersionAndOptOutPersist()
    {
        using var factory = new ApiFactory();
        using var client = SpecializedTestApi.Client(factory);
        var task = await SpecializedTestApi.Import(client, "Apple");
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{task}/reference");
        Assert.Equal(Glossary.DefaultId, before.GetProperty("glossaryId").GetGuid());
        await SetReference(client, task, null, "   ");
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{task}/reference");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("glossaryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("backgroundInformation").ValueKind);
        Assert.Equal(2, after.GetProperty("version").GetInt32());
        await Error(await client.PutAsJsonAsync($"/api/tasks/{task}/reference", new { glossaryId = (Guid?)null, backgroundInformation = "stale", version = 1 }), "task_reference_version_conflict", 409);
        await Error(await client.PutAsJsonAsync($"/api/tasks/{task}/reference", new { glossaryId = Guid.NewGuid(), backgroundInformation = "x", version = 2 }), "glossary_not_found", 404);
        await Error(await client.PutAsJsonAsync($"/api/tasks/{task}/reference", new { glossaryId = (Guid?)null, backgroundInformation = new string('长', 4001), version = 2 }), "invalid_background_information", 400);
        await Error(await client.PutAsJsonAsync($"/api/tasks/{task}/reference", new { glossaryId = (Guid?)null, backgroundInformation = 42, version = 2 }), "invalid_background_information", 400);
    }

    [Theory]
    [InlineData("general", "openai")]
    [InlineData("hy-mt2", "openai")]
    [InlineData("bilibili-index-translate", "openai")]
    [InlineData("hy-mt2", "ollama")]
    [InlineData("bilibili-index-translate", "ollama")]
    public async Task TranslationUsesSelectedTermsAndBackgroundInCorrectMessage(string format, string kind)
    {
        using var handler = new ReferenceHandler(kind);
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format, kind);
        var selected = await GlossaryAsync(client, "食物");
        await TermAsync(client, selected, "Apple", "苹果");
        await TermAsync(client, Glossary.DefaultId, "Apple", "无关译法");
        var task = await SpecializedTestApi.Import(client, "Apple");
        await SetReference(client, task, selected, "这是水果童话");
        Assert.Equal("completed", (await SpecializedTranslationExecutionTests.Run(client, task)).GetProperty("status").GetString());
        var request = Assert.Single(handler.Requests);
        var messages = request.GetProperty("messages");
        var first = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("这是水果童话", first);
        if (format == "general")
        {
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            var segment = JsonSerializer.Deserialize<JsonElement>(messages[1].GetProperty("content").GetString()!).GetProperty("segments")[0];
            Assert.Equal("Apple", segment.GetProperty("sourceText").GetString());
            Assert.Equal("苹果", Assert.Single(segment.GetProperty("terms").EnumerateArray()).GetProperty("targetTerm").GetString());
        }
        else
        {
            Assert.Single(messages.EnumerateArray());
            Assert.Contains("苹果", first);
            Assert.DoesNotContain("无关译法", first);
            Assert.Equal("user", messages[0].GetProperty("role").GetString());
            Assert.Contains(format == "hy-mt2" ? "【背景信息】" : "【约束要求】", first);
            if (format == "hy-mt2") Assert.Contains("【待翻译文本】", first);
            else Assert.True(first.IndexOf("这是水果童话", StringComparison.Ordinal) > first.IndexOf("【约束要求】", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ReviewSnapshotsReasoningTermsAndBackgroundAndProtectsBusyTask()
    {
        using var handler = new ReferenceHandler("openai") { InvalidFirst = true };
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        var (provider, model) = await SpecializedTestApi.Configure(client, "general");
        var glossary = await GlossaryAsync(client, "水果");
        await TermAsync(client, glossary, "Apple", "苹果");
        var task = await SpecializedTestApi.Import(client, "Apple");
        await SetReference(client, task, glossary, "这是水果童话");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Models.SingleAsync()).SupportsThinking = true;
        (await db.TranslationSegments.SingleAsync()).TargetText = "苹果";
        await db.SaveChangesAsync();
        var service = new ReviewRunService(db, new ReviewRunQueue(), scope.ServiceProvider.GetRequiredService<TaskOperationLock>());
        var run = await service.CreateAsync(task, new(1, "en", "zh-CN", provider, model, "high"), default);
        await Error(await client.PutAsJsonAsync($"/api/tasks/{task}/reference", new { glossaryId = glossary, backgroundInformation = "changed", version = 2 }), "task_busy", 409);
        (await db.Terms.SingleAsync()).TargetTerm = "后续译法";
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ReviewRunProcessor>().ProcessAsync(run.RunId, default);
        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal("high", request.GetProperty("reasoning_effort").GetString());
            Assert.False(request.TryGetProperty("temperature", out _));
            Assert.Contains("这是水果童话", request.GetProperty("messages")[0].GetProperty("content").GetString());
            var segment = JsonSerializer.Deserialize<JsonElement>(request.GetProperty("messages")[1].GetProperty("content").GetString()!).GetProperty("segments")[0];
            Assert.Equal("苹果", Assert.Single(segment.GetProperty("terms").EnumerateArray()).GetProperty("targetTerm").GetString());
        }
        var result = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{task}/review-runs/{run.RunId}");
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Equal("high", result.GetProperty("reasoningEffort").GetString());
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{task}/review-runs");
        Assert.Equal("high", list.GetProperty("items")[0].GetProperty("reasoningEffort").GetString());
    }

    [Theory]
    [InlineData("general")]
    [InlineData("hy-mt2")]
    [InlineData("bilibili-index-translate")]
    public async Task OptOutAndEmptyBackgroundDoNotInjectReferenceMaterial(string format)
    {
        using var handler = new ReferenceHandler("openai");
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format);
        await TermAsync(client, Glossary.DefaultId, "Apple", "不应出现的词条");
        var task = await SpecializedTestApi.Import(client, "Apple");
        await SetReference(client, task, null, "  ");
        await SpecializedTranslationExecutionTests.Run(client, task);
        var messages = Assert.Single(handler.Requests).GetProperty("messages");
        foreach (var message in messages.EnumerateArray())
        {
            var text = message.GetProperty("content").GetString()!;
            Assert.DoesNotContain("背景", text);
            Assert.DoesNotContain("参考下面的翻译", text);
            Assert.DoesNotContain("专名/术语对照:", text);
        }
        if (format == "general")
            Assert.Empty(JsonSerializer.Deserialize<JsonElement>(messages[1].GetProperty("content").GetString()!).GetProperty("segments")[0].GetProperty("terms").EnumerateArray());
    }

    [Theory]
    [InlineData("hy-mt2")]
    [InlineData("bilibili-index-translate")]
    public async Task BackgroundCountsTowardBudgetWithoutTruncation(string format)
    {
        using var handler = new ReferenceHandler("openai");
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, format);
        var task = await SpecializedTestApi.Import(client, new string('A', 1000));
        await SetReference(client, task, null, new string('背', 4000));
        var run = await SpecializedTranslationExecutionTests.Run(client, task);
        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Equal("translation_request_too_large", run.GetProperty("failure").GetProperty("code").GetString());
        Assert.Empty(handler.Requests);
        var reference = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{task}/reference");
        Assert.Equal(4000, reference.GetProperty("backgroundInformation").GetString()!.Length);
    }

    [Theory]
    [InlineData("general")]
    [InlineData("hy-mt2")]
    public async Task TranslationUsesCreationSnapshotAcrossRetries(string format)
    {
        using var handler = new ReferenceHandler("openai") { InvalidFirst = true };
        using var factory = new ApiFactory("Development", null, translationHandler: handler, seedTranslationConfiguration: false);
        using var client = SpecializedTestApi.Client(factory);
        var (provider, model) = await SpecializedTestApi.Configure(client, format);
        await TermAsync(client, Glossary.DefaultId, "Apple", "苹果");
        var task = await SpecializedTestApi.Import(client, "Apple");
        await SetReference(client, task, Glossary.DefaultId, "创建时背景");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new TranslationRunService(db, new TranslationRunQueue(), scope.ServiceProvider.GetRequiredService<TaskOperationLock>());
        var run = await service.CreateAsync(task, new(1, "en", "zh-CN", provider, model), default);
        (await db.Terms.SingleAsync()).TargetTerm = "后续译法";
        await db.SaveChangesAsync();
        await Error(await client.PutAsJsonAsync($"/api/tasks/{task}/reference", new
        { glossaryId = (Guid?)null, backgroundInformation = "changed", version = 2 }), "task_busy", 409);
        await scope.ServiceProvider.GetRequiredService<TranslationRunProcessor>().ProcessAsync(run.RunId, default);
        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        var messages = requests[0].GetProperty("messages");
        Assert.Contains("创建时背景", messages[0].GetProperty("content").GetString());
        Assert.Equal(messages.GetRawText(), requests[1].GetProperty("messages").GetRawText());
        if (format == "general")
            Assert.Equal("苹果", JsonSerializer.Deserialize<JsonElement>(messages[1].GetProperty("content").GetString()!).GetProperty("segments")[0].GetProperty("terms")[0].GetProperty("targetTerm").GetString());
        else
        {
            Assert.Contains("Apple 翻译成 苹果", messages[0].GetProperty("content").GetString());
            Assert.DoesNotContain("后续译法", messages[0].GetProperty("content").GetString());
        }
    }

    [Theory]
    [InlineData("openai", false, "high", "reasoning_not_supported", 422)]
    [InlineData("ollama", true, "xhigh", "reasoning_not_supported", 422)]
    [InlineData("openai", true, "bogus", "invalid_reasoning_effort", 400)]
    public async Task ReviewValidatesReasoning(string kind, bool thinking, string effort, string code, int status)
    {
        using var factory = new ApiFactory();
        using var client = SpecializedTestApi.Client(factory);
        await SpecializedTestApi.Configure(client, "general", kind);
        var task = await SpecializedTestApi.Import(client, "Apple");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Models.SingleAsync()).SupportsThinking = thinking;
        (await db.TranslationSegments.SingleAsync()).TargetText = "苹果";
        await db.SaveChangesAsync();
        await Error(await client.PostAsJsonAsync($"/api/tasks/{task}/review-runs", new
        { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN", reasoningEffort = effort }), code, status);
        Assert.Empty(await db.ReviewRuns.ToArrayAsync());
    }

    [Fact]
    public async Task MigrationKeepsExistingTermsAndTaskContent()
    {
        using var factory = new ApiFactory();
        using var client = SpecializedTestApi.Client(factory);
        var task = await SpecializedTestApi.Import(client, "Apple");
        var term = await TermAsync(client, Glossary.DefaultId, "Apple", "苹果");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bytes = (await db.TranslationTasks.AsNoTracking().SingleAsync()).SourceBytes;
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261027000000_AddTranslationMessageFormat");
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var restored = await db.TranslationTasks.SingleAsync();
        Assert.Equal(task, restored.Id);
        Assert.Equal(bytes, restored.SourceBytes);
        Assert.Equal(Glossary.DefaultId, restored.GlossaryId);
        Assert.Equal(Glossary.DefaultId, (await db.Terms.SingleAsync(t => t.Id == term)).GlossaryId);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<Guid> GlossaryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/glossaries", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<Guid> TermAsync(HttpClient client, Guid glossaryId, string source, string target)
    {
        var response = await client.PostAsJsonAsync("/api/terms", new { glossaryId, sourceTerm = source, targetTerm = target, sourceLanguage = "en", targetLanguage = "zh-CN" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task SetReference(HttpClient client, Guid task, Guid? glossary, string? background) =>
        (await client.PutAsJsonAsync($"/api/tasks/{task}/reference", new { glossaryId = glossary, backgroundInformation = background, version = 1 })).EnsureSuccessStatusCode();
    private static async Task Error(HttpResponseMessage response, string code, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private sealed class ReferenceHandler(string kind) : HttpMessageHandler
    {
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        public bool InvalidFirst { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Enqueue(body);
            var messages = body.GetProperty("messages");
            var content = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
            var text = "苹果";
            if (content.StartsWith('{'))
            {
                var segments = JsonSerializer.Deserialize<JsonElement>(content).GetProperty("segments").EnumerateArray().ToArray();
                text = segments[0].TryGetProperty("targetText", out _)
                    ? JsonSerializer.Serialize(new { reviews = segments.Select(s => new { segmentId = s.GetProperty("segmentId").GetGuid(), comments = Array.Empty<object>() }) })
                    : JsonSerializer.Serialize(new { translations = segments.Select(s => new { segmentId = s.GetProperty("segmentId").GetGuid(), targetText = "苹果" }) });
            }
            if (InvalidFirst && Requests.Count == 1) text = "";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = kind == "openai" ? JsonContent.Create(new
                {
                    id = "test", @object = "chat.completion", created = 1, model = "test",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } }
                }) : JsonContent.Create(new
                {
                    model = "test", created_at = DateTimeOffset.UtcNow,
                    message = new { role = "assistant", content = text }, done = true, done_reason = "stop"
                })
            };
        }
    }
}

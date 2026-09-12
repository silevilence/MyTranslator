using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;
using MyTranslator.Api.FileTasks;
using MyTranslator.Api.TaskOperations;

namespace MyTranslator.Api.Tests;

public sealed class SegmentEditingTests
{
    [Fact]
    public async Task SaveConfirmUnconfirmClearAndResultAreConsistent()
    {
        using var factory = new ApiFactory();
        using var client = RuleCheckTests.CreateClient(factory);
        var id = await RuleCheckTests.ImportAsync(client, "Hello");
        var segment = (await Segments(client, id))[0];
        var saved = await Save(client, id, segment, "你好", "translated");
        Assert.Equal(2, saved.Version);
        var confirmed = await Save(client, id, saved, "你好", "confirmed");
        Assert.Equal(3, confirmed.Version);
        Assert.Equal("confirmed", (await Segments(client, id))[0].ConfirmationStatus);
        var task = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}");
        Assert.Equal("completed", task.GetProperty("status").GetString());
        Assert.Equal(1, task.GetProperty("progress").GetProperty("confirmedSegments").GetInt32());
        Assert.Equal(100, task.GetProperty("progress").GetProperty("percent").GetDouble());
        Assert.Equal("你好", await client.GetStringAsync($"/api/tasks/{id}/result"));
        Assert.Equal(3, (await Save(client, id, confirmed, "你好", "confirmed")).Version);
        var unconfirmed = await Save(client, id, confirmed, "你好", "translated");
        var cleared = await Save(client, id, unconfirmed, "  ", "pending");
        Assert.Null(cleared.TargetText);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/tasks/{id}/result")).StatusCode);
        var tm = await client.GetFromJsonAsync<JsonElement>("/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN");
        var entry = Assert.Single(tm.GetProperty("items").EnumerateArray());
        Assert.Equal("confirmed_segment", entry.GetProperty("origin").GetString());
        Assert.Equal(segment.Id, entry.GetProperty("originSegmentId").GetGuid());
        Assert.Equal("created", (await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task BatchConfirmationIsAtomicAndDeduplicatesTm()
    {
        using var factory = new ApiFactory();
        using var client = RuleCheckTests.CreateClient(factory);
        var id = await RuleCheckTests.ImportAsync(client, "Same\n\nSame");
        var rows = await Segments(client, id);
        rows[0] = await Save(client, id, rows[0], "相同", "translated");
        var bad = await client.PostAsJsonAsync($"/api/tasks/{id}/segment-confirmations",
            new ConfirmSegmentsRequest(1, true, rows.Select(s => new SegmentVersion(s.Id, s.Version)).ToArray(), "en", "zh-CN"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("translated", (await Segments(client, id))[0].ConfirmationStatus);
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().TranslationMemoryEntries.ToListAsync());
        rows[1] = await Save(client, id, rows[1], "相同", "translated");
        var success = await client.PostAsJsonAsync($"/api/tasks/{id}/segment-confirmations",
            new ConfirmSegmentsRequest(1, true, rows.Select(s => new SegmentVersion(s.Id, s.Version)).ToArray(), "en", "zh-CN"));
        success.EnsureSuccessStatusCode();
        rows = (await Segments(client, id));
        Assert.All(rows, s => Assert.Equal("confirmed", s.ConfirmationStatus));
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().TranslationMemoryEntries.ToListAsync());
        var undo = await client.PostAsJsonAsync($"/api/tasks/{id}/segment-confirmations",
            new ConfirmSegmentsRequest(1, false, rows.Select(s => new SegmentVersion(s.Id, s.Version)).ToArray()));
        undo.EnsureSuccessStatusCode();
        Assert.All(await Segments(client, id), s => Assert.Equal("translated", s.ConfirmationStatus));
    }

    [Fact]
    public async Task RejectsLostUpdatesMissingLanguagesImmutableFieldsAndInvalidStates()
    {
        using var factory = new ApiFactory();
        using var client = RuleCheckTests.CreateClient(factory);
        var id = await RuleCheckTests.ImportAsync(client, "Hello");
        var row = (await Segments(client, id))[0];
        var url = $"/api/tasks/{id}/segments/{row.Id}";
        var request = new SaveSegmentRequest(1, 1, "你好", "translated");
        await AssertCode(client.PutAsJsonAsync(url, request with { ExtractionRevision = 0 }), "invalid_extraction_revision", 400);
        await AssertCode(client.PutAsJsonAsync(url, request with { ExtractionRevision = 2 }), "extraction_revision_changed", 409);
        await AssertCode(client.PutAsJsonAsync(url, request with { Version = 0 }), "invalid_segment_version", 400);
        await AssertCode(client.PutAsJsonAsync(url, request with { Version = 2 }), "segment_version_conflict", 409);
        await AssertCode(client.PutAsJsonAsync(url, request with { ConfirmationStatus = "confirmed" }), "tm_language_pair_required", 422);
        await AssertCode(client.PutAsJsonAsync(url, request with { ConfirmationStatus = "confirmed", SourceLanguage = "en", TargetLanguage = "en" }), "invalid_language_tag", 400);
        await AssertCode(client.PutAsJsonAsync(url, request with { ConfirmationStatus = "confirmed", SourceLanguage = "bad_", TargetLanguage = "zh" }), "invalid_language_tag", 400);
        await AssertCode(client.PutAsJsonAsync(url, request with { ConfirmationStatus = "unknown" }), "invalid_confirmation_status", 400);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url,
            new { extractionRevision = 1, version = 1, targetText = "text", confirmationStatus = "translated", markupTable = new object[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url,
            new { extractionRevision = 1, version = 1, confirmationStatus = "pending" })).StatusCode);
        Assert.Equal(1, (await Segments(client, id))[0].Version);
        await AssertCode(client.PutAsJsonAsync($"/api/tasks/{id}/segments/{Guid.NewGuid()}", request), "segment_not_found", 404);
        await AssertCode(client.PutAsJsonAsync($"/api/tasks/{Guid.NewGuid()}/segments/{row.Id}", request), "task_not_found", 404);
        await Save(client, id, row, "你好", "translated");
        await AssertCode(client.PutAsJsonAsync(url, request), "segment_version_conflict", 409);
    }

    [Fact]
    public async Task RejectsBrokenMarkupButPreservesReviewCommentsAndSource()
    {
        using var factory = new ApiFactory();
        using var client = RuleCheckTests.CreateClient(factory);
        var id = await RuleCheckTests.ImportAsync(client, "# Hello", "sample.md");
        var row = (await Segments(client, id))[0];
        await AssertCode(client.PutAsJsonAsync($"/api/tasks/{id}/segments/{row.Id}", new SaveSegmentRequest(1, 1, "broken", "translated")),
            "placeholder_integrity_violation", 422);
        var target = row.SourceText.Replace("Hello", "你好", StringComparison.Ordinal);
        var saved = await Save(client, id, row, target, "confirmed");
        Assert.Equal(row.SourceText, saved.SourceText);
        Assert.Equal(row.MarkupTable.GetRawText(), saved.MarkupTable.GetRawText());
        Assert.Equal("# 你好", await client.GetStringAsync($"/api/tasks/{id}/result"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedTranslationAndReviewBlockEditing(bool review)
    {
        using var factory = new ApiFactory();
        using var client = RuleCheckTests.CreateClient(factory);
        var id = await RuleCheckTests.ImportAsync(client, "Hello");
        var row = (await Segments(client, id))[0];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (review) db.ReviewRuns.Add(new ReviewRun { Id = Guid.NewGuid(), TaskId = id, ActiveTaskLockId = id, ExtractionRevision = 1, TargetLanguage = "zh", CreatedAt = DateTimeOffset.UtcNow });
            else db.TranslationRuns.Add(new TranslationRun { Id = Guid.NewGuid(), TaskId = id, ActiveTaskLockId = id, ExtractionRevision = 1, TargetLanguage = "zh", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await AssertCode(client.PutAsJsonAsync($"/api/tasks/{id}/segments/{row.Id}", new SaveSegmentRequest(1, 1, "你好", "translated")), "task_busy", 409);
    }

    [Fact]
    public async Task ThirdPartyCanTranslateEditConfirmAndDownload()
    {
        using var factory = new ApiFactory("Development", null, translationProvider: new Translator());
        using var client = RuleCheckTests.CreateClient(factory);
        var id = await RuleCheckTests.ImportAsync(client, "Hello");
        var start = await client.PostAsJsonAsync($"/api/tasks/{id}/translation-runs", new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        JsonElement run;
        do
        {
            await Task.Delay(25, timeout.Token);
            run = await client.GetFromJsonAsync<JsonElement>(start.Headers.Location, timeout.Token);
        } while (run.GetProperty("status").GetString() is "queued" or "processing");
        Assert.Equal("completed", run.GetProperty("status").GetString());
        var row = (await Segments(client, id))[0];
        Assert.Equal("你好", row.TargetText);
        await Save(client, id, row, "您好", "confirmed");
        Assert.Equal("您好", await client.GetStringAsync($"/api/tasks/{id}/result"));
    }

    [Fact]
    public async Task BatchAndResultRejectInvalidRequestsAndInFlightLock()
    {
        using var factory = new ApiFactory();
        using var client = RuleCheckTests.CreateClient(factory);
        var id = await RuleCheckTests.ImportAsync(client, "Hello");
        var row = (await Segments(client, id))[0];
        var url = $"/api/tasks/{id}/segment-confirmations";
        foreach (var items in new IReadOnlyList<SegmentVersion>?[] { null, [], [new(row.Id, 1), new(row.Id, 1)] })
            await AssertCode(client.PostAsJsonAsync(url, new ConfirmSegmentsRequest(1, false, items)), "invalid_segment_batch", 400);
        await AssertCode(client.PostAsJsonAsync(url, new ConfirmSegmentsRequest(1, false, [new(Guid.NewGuid(), 1)])), "segment_not_found", 404);
        (await client.PostAsJsonAsync(url, new ConfirmSegmentsRequest(1, false, [new(row.Id, 1)]))).EnsureSuccessStatusCode();
        await using (var lease = factory.Services.GetRequiredService<TaskOperationLock>().TryAcquire(id))
        {
            await AssertCode(client.PutAsJsonAsync($"/api/tasks/{id}/segments/{row.Id}", new SaveSegmentRequest(1, 1, "你好", "translated")), "task_busy", 409);
            await AssertCode(client.PostAsJsonAsync(url, new ConfirmSegmentsRequest(1, false, [new(row.Id, 1)])), "task_busy", 409);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tasks/{Guid.NewGuid()}/result")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/tasks/{id}/segments/{row.Id}", new SaveSegmentRequest(1, 1, "你好", "translated"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, new ConfirmSegmentsRequest(1, false, [new(row.Id, 1)]))).StatusCode);
    }

    [Fact]
    public async Task ConfirmedEditsRetainHistoryAndMemoryFailureRollsBack()
    {
        using var factory = new ApiFactory();
        using var client = RuleCheckTests.CreateClient(factory);
        var id = await RuleCheckTests.ImportAsync(client, "Hello");
        var row = await Save(client, id, (await Segments(client, id))[0], "你好", "confirmed");
        row = await Save(client, id, row, "您好", "confirmed");
        await AssertCode(client.PutAsJsonAsync($"/api/tasks/{id}/segments/{row.Id}",
            new SaveSegmentRequest(1, row.Version, new string('x', 20001), "confirmed", "en", "zh-CN")), "invalid_tm_target_text", 400);
        Assert.Equal("您好", (await Segments(client, id))[0].TargetText);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AppDbContext>().TranslationMemoryEntries.CountAsync());
    }

    internal static async Task<SegmentResponse[]> Segments(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<SegmentPage>($"/api/tasks/{id}/segments"))!.Items.ToArray();
    internal static async Task<SegmentResponse> Save(HttpClient client, Guid taskId, SegmentResponse segment, string? target, string status)
    {
        var response = await client.PutAsJsonAsync($"/api/tasks/{taskId}/segments/{segment.Id}",
            new SaveSegmentRequest(1, segment.Version, target, status, "en", "zh-CN"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SegmentResponse>())!;
    }
    private static async Task AssertCode(Task<HttpResponseMessage> action, string code, int status)
    {
        var response = await action;
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
    private sealed class Translator : TestTranslationChatClient
    {
        protected override Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(TestTranslationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TestTranslationOutput>>(request.Segments.Select(s => new TestTranslationOutput(s.SegmentId, "你好")).ToArray());
    }
}

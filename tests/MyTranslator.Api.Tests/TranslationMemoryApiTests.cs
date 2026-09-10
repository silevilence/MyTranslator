using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Tests;

public sealed class TranslationMemoryApiTests
{
    private const string DevelopmentToken = "sk-dev-00000000000000000000000000000000";

    [Fact]
    public async Task ExplicitEntryBatchIsIdempotentThroughThePublicApi()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var request = new
        {
            items = new[]
            {
                new
                {
                    sourceText = "Use translation memory.",
                    targetText = "使用翻译记忆库。",
                    sourceLanguage = "en",
                    targetLanguage = "zh-CN",
                    markupTable = Array.Empty<object>()
                }
            }
        };

        var createdResponse = await client.PostAsJsonAsync("/api/tm/entries", request);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.Equal(1, created.GetProperty("summary").GetProperty("submitted").GetInt32());
        Assert.Equal(1, created.GetProperty("summary").GetProperty("created").GetInt32());
        Assert.Equal(0, created.GetProperty("summary").GetProperty("duplicates").GetInt32());
        var createdItem = Assert.Single(created.GetProperty("items").EnumerateArray());
        Assert.Equal(0, createdItem.GetProperty("index").GetInt32());
        Assert.Equal("created", createdItem.GetProperty("disposition").GetString());
        var entry = createdItem.GetProperty("entry");
        Assert.Equal("external", entry.GetProperty("origin").GetString());
        Assert.Equal("en", entry.GetProperty("sourceLanguage").GetString());
        Assert.Equal("zh-CN", entry.GetProperty("targetLanguage").GetString());
        Assert.Equal(JsonValueKind.Array, entry.GetProperty("markupTable").ValueKind);

        var duplicateResponse = await client.PostAsJsonAsync("/api/tm/entries", request);
        var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        Assert.Equal(0, duplicate.GetProperty("summary").GetProperty("created").GetInt32());
        Assert.Equal(1, duplicate.GetProperty("summary").GetProperty("duplicates").GetInt32());
        var duplicateItem = Assert.Single(duplicate.GetProperty("items").EnumerateArray());
        Assert.Equal("duplicate", duplicateItem.GetProperty("disposition").GetString());
        Assert.Equal(entry.GetProperty("id").GetGuid(), duplicateItem.GetProperty("entry").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ConcurrentIdenticalBatchesResolveToOneEntry()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var request = new
        {
            items = new[] { Entry("Concurrent memory.", "并发记忆。", "zh-CN") }
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync("/api/tm/entries", request)));
        var bodies = await Task.WhenAll(responses
            .Select(response => response.Content.ReadFromJsonAsync<JsonElement>()));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(4, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Single(bodies.Select(body => body.GetProperty("items")[0]
                .GetProperty("entry")
                .GetProperty("id")
                .GetGuid())
            .Distinct());
    }

    [Fact]
    public async Task EntriesCanBeListedWithLanguageBoundFuzzyPagination()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var createResponse = await client.PostAsJsonAsync(
            "/api/tm/entries",
            new
            {
                items = new object[]
                {
                    Entry("Use translation memory.", "使用翻译记忆库。", "zh-CN"),
                    Entry("Store translated segments.", "保存已翻译分段。", "zh-CN"),
                    Entry("Use translation memory.", "翻訳メモリを使用する。", "ja")
                }
            });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var zhIds = created.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("entry"))
            .Where(entry => entry.GetProperty("targetLanguage").GetString() == "zh-CN")
            .Select(entry => entry.GetProperty("id").GetGuid())
            .ToHashSet();

        var fuzzyResponse = await client.GetAsync(
            "/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN&query=Use%20translation%20memory!&limit=20");
        var fuzzy = await fuzzyResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, fuzzyResponse.StatusCode);
        var match = Assert.Single(fuzzy.GetProperty("items").EnumerateArray());
        Assert.Contains(match.GetProperty("id").GetGuid(), zhIds);
        Assert.InRange(match.GetProperty("sourceMatchScore").GetDouble(), 0.95, 0.96);
        Assert.Equal(JsonValueKind.Null, fuzzy.GetProperty("nextCursor").ValueKind);

        var firstResponse = await client.GetAsync(
            "/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN&limit=1");
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        var firstItem = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, firstItem.GetProperty("sourceMatchScore").ValueKind);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var secondResponse = await client.GetAsync(
            $"/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondItem = Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal(zhIds, new[]
        {
            firstItem.GetProperty("id").GetGuid(),
            secondItem.GetProperty("id").GetGuid()
        }.ToHashSet());

        var reboundResponse = await client.GetAsync(
            $"/api/tm/entries?sourceLanguage=en&targetLanguage=ja&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        var problem = await reboundResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, reboundResponse.StatusCode);
        Assert.Equal("invalid_cursor", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EntryCanBeReadAndDeletedWithoutChangingOtherResources()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var createResponse = await client.PostAsJsonAsync(
            "/api/tm/entries",
            new { items = new[] { Entry("Read this history.", "读取这条历史。", "zh-CN") } });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("items")[0].GetProperty("entry").GetProperty("id").GetGuid();

        var readResponse = await client.GetAsync($"/api/tm/entries/{id}");
        var entry = await readResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal("Read this history.", entry.GetProperty("sourceText").GetString());
        Assert.False(entry.TryGetProperty("sourceMatchScore", out _));

        var deleteResponse = await client.DeleteAsync($"/api/tm/entries/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var missingResponse = await client.GetAsync($"/api/tm/entries/{id}");
        var problem = await missingResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal("tm_entry_not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenericComparisonReturnsMatchesScoresAndServerWarningDecision()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var createResponse = await client.PostAsJsonAsync(
            "/api/tm/entries",
            new { items = new[] { Entry("Use translation memory.", "使用翻译记忆库。", "zh-CN") } });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var response = await client.PostAsJsonAsync(
            "/api/tm/comparisons",
            new
            {
                sourceText = "Use translation memory.",
                targetText = "使用翻译缓存。",
                sourceLanguage = "en",
                targetLanguage = "zh-CN",
                markupTable = Array.Empty<object>(),
                limit = 5
            });
        var comparison = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("taskId").ValueKind);
        Assert.Equal(0.7, comparison.GetProperty("thresholds").GetProperty("minimumSourceMatchScore").GetDouble());
        Assert.Equal(0.85, comparison.GetProperty("thresholds").GetProperty("warningSourceMatchScore").GetDouble());
        Assert.Equal(0.3, comparison.GetProperty("thresholds").GetProperty("warningTargetDifference").GetDouble());
        var match = Assert.Single(comparison.GetProperty("items").EnumerateArray());
        Assert.Equal(1.0, match.GetProperty("sourceMatchScore").GetDouble());
        Assert.Equal(0.625, match.GetProperty("targetSimilarity").GetDouble());
        Assert.Equal(0.375, match.GetProperty("targetDifference").GetDouble());
        var warning = comparison.GetProperty("differenceWarning");
        Assert.True(warning.GetProperty("hasWarning").GetBoolean());
        Assert.Equal(match.GetProperty("entryId").GetGuid(), warning.GetProperty("referenceEntryId").GetGuid());
        Assert.Equal("target_difference_exceeded", warning.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task TaskSegmentComparisonUsesSavedTextAndRejectsStaleRevisionOrVersion()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            translationProvider: new MemoryComparisonTranslationClient());
        using var client = CreateClient(factory);
        var createEntryResponse = await client.PostAsJsonAsync(
            "/api/tm/entries",
            new { items = new[] { Entry("Use translation memory.", "使用翻译记忆库。", "zh-CN") } });
        Assert.Equal(HttpStatusCode.Created, createEntryResponse.StatusCode);
        var taskId = await ImportMarkdownAsync(client, "Use translation memory.");
        var runResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var run = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Accepted, runResponse.StatusCode);
        await WaitForTerminalRunAsync(client, taskId, run.GetProperty("runId").GetGuid());
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(segments.GetProperty("items").EnumerateArray());
        var segmentId = segment.GetProperty("id").GetGuid();
        var segmentVersion = segment.GetProperty("version").GetInt32();

        var response = await client.GetAsync(
            $"/api/tasks/{taskId}/segments/{segmentId}/tm-comparison?extractionRevision=1&segmentVersion={segmentVersion}&sourceLanguage=en&targetLanguage=zh-CN&limit=5");
        var comparison = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(taskId, comparison.GetProperty("taskId").GetGuid());
        Assert.Equal(segmentId, comparison.GetProperty("segmentId").GetGuid());
        Assert.Equal(segmentVersion, comparison.GetProperty("segmentVersion").GetInt32());
        Assert.True(comparison.GetProperty("differenceWarning").GetProperty("hasWarning").GetBoolean());

        var staleVersionResponse = await client.GetAsync(
            $"/api/tasks/{taskId}/segments/{segmentId}/tm-comparison?extractionRevision=1&segmentVersion=1&sourceLanguage=en&targetLanguage=zh-CN");
        var staleVersion = await staleVersionResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, staleVersionResponse.StatusCode);
        Assert.Equal("segment_version_conflict", staleVersion.GetProperty("code").GetString());
        Assert.Equal(segmentVersion, staleVersion.GetProperty("errors").GetProperty("currentVersion").GetInt32());

        var staleRevisionResponse = await client.GetAsync(
            $"/api/tasks/{taskId}/segments/{segmentId}/tm-comparison?extractionRevision=2&segmentVersion={segmentVersion}&sourceLanguage=en&targetLanguage=zh-CN");
        var staleRevision = await staleRevisionResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, staleRevisionResponse.StatusCode);
        Assert.Equal("extraction_revision_changed", staleRevision.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PlaceholderViolationRejectsTheWholeEntryBatch()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var markup = new[]
        {
            new
            {
                id = 1,
                kind = "paired",
                openingText = "<strong>",
                closingText = "</strong>",
                originalText = (string?)null,
                meaning = "加粗强调"
            }
        };
        var response = await client.PostAsJsonAsync(
            "/api/tm/entries",
            new
            {
                items = new object[]
                {
                    new
                    {
                        sourceText = "<x1>Valid</x1>",
                        targetText = "<x1>有效</x1>",
                        sourceLanguage = "en",
                        targetLanguage = "zh-CN",
                        markupTable = markup
                    },
                    new
                    {
                        sourceText = "<x1>Broken</x1>",
                        targetText = "<x1>损坏",
                        sourceLanguage = "en",
                        targetLanguage = "zh-CN",
                        markupTable = markup
                    }
                }
            });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("tm_placeholder_integrity_violation", problem.GetProperty("code").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>(
            "/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN");
        Assert.Empty(list.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task MatchingIgnoresOnlyReferencesRegisteredByEachMarkupTable()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var markup = new[]
        {
            new
            {
                id = 1,
                kind = "paired",
                openingText = "<strong>",
                closingText = "</strong>",
                originalText = (string?)null,
                meaning = "bold"
            }
        };
        var createResponse = await client.PostAsJsonAsync(
            "/api/tm/entries",
            new
            {
                items = new object[]
                {
                    new
                    {
                        sourceText = "Hello <x1>World</x1>",
                        targetText = "你好 <x1>世界</x1>",
                        sourceLanguage = "en",
                        targetLanguage = "zh-CN",
                        markupTable = markup
                    },
                    Entry("Keep <x99> literal.", "保留 <x99> 字面文本。", "zh-CN")
                }
            });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var registeredResponse = await client.GetAsync(
            "/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN&query=Hello%20World");
        var registered = await registeredResponse.Content.ReadFromJsonAsync<JsonElement>();
        var registeredMatch = Assert.Single(registered.GetProperty("items").EnumerateArray());
        Assert.Equal(1.0, registeredMatch.GetProperty("sourceMatchScore").GetDouble());

        var literalResponse = await client.GetAsync(
            "/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN&query=Keep%20%3Cx42%3E%20literal.");
        var literal = await literalResponse.Content.ReadFromJsonAsync<JsonElement>();
        var literalMatch = Assert.Single(literal.GetProperty("items").EnumerateArray());
        Assert.InRange(literalMatch.GetProperty("sourceMatchScore").GetDouble(), 0.89, 0.90);
    }

    /// <summary>
    /// §6.1 读路径不校验服务端已存标记表：修复前抽取实现写下的 paired 缺 closingText 的历史数据
    /// 仍应能参与对比，不得返回调用方无法修正的 4xx。
    /// </summary>
    [Fact]
    public async Task SegmentComparisonToleratesStoredMarkupTableDefects()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var createEntryResponse = await client.PostAsJsonAsync(
            "/api/tm/entries",
            new { items = new[] { Entry("Use translation memory.", "使用翻译记忆库。", "zh-CN") } });
        Assert.Equal(HttpStatusCode.Created, createEntryResponse.StatusCode);
        var taskId = await ImportMarkdownAsync(client, "Use translation memory.");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await database.TranslationSegments.FirstAsync(item => item.TaskId == taskId);
            stored.MarkupTableJson =
                """[{"id":1,"kind":"paired","openingText":"<p>","closingText":null,"originalText":null,"meaning":"HTML p 标签"}]""";
            await database.SaveChangesAsync();
        }

        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(segments.GetProperty("items").EnumerateArray());
        var response = await client.GetAsync(
            $"/api/tasks/{taskId}/segments/{segment.GetProperty("id").GetGuid()}/tm-comparison" +
            $"?extractionRevision=1&segmentVersion={segment.GetProperty("version").GetInt32()}&sourceLanguage=en&targetLanguage=zh-CN");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var comparison = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = Assert.Single(comparison.GetProperty("items").EnumerateArray());
        Assert.Equal(1.0, match.GetProperty("sourceMatchScore").GetDouble());
    }

    /// <summary>
    /// 打分长度预筛的边界：相似度上界为「较短长度 / 较长长度」，恰好高于门槛时仍须返回该匹配，
    /// 低于门槛时才允许短路（不得把 0.70 附近的匹配筛掉）。
    /// </summary>
    [Fact]
    public async Task LengthPreFilterKeepsMatchAtTheThresholdBoundary()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var createResponse = await client.PostAsJsonAsync(
            "/api/tm/entries",
            new
            {
                items = new[]
                {
                    Entry("abcdefghijklmn", "上界 0.7143。", "zh-CN"),
                    Entry("abcdefghijklmnop", "上界 0.6250。", "zh-CN")
                }
            });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var response = await client.GetAsync(
            "/api/tm/entries?sourceLanguage=en&targetLanguage=zh-CN&query=abcdefghij");
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal("abcdefghijklmn", match.GetProperty("sourceText").GetString());
        Assert.Equal(0.7143, match.GetProperty("sourceMatchScore").GetDouble());
    }

    private static object Entry(string sourceText, string targetText, string targetLanguage) => new
    {
        sourceText,
        targetText,
        sourceLanguage = "en",
        targetLanguage,
        markupTable = Array.Empty<object>()
    };

    private static async Task<Guid> ImportMarkdownAsync(HttpClient client, string text)
    {
        using var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        request.Add(file, "file", "tm.md");
        request.Add(new StringContent("markdown"), "fileType");
        var response = await client.PostAsync("/api/tasks/imports/file", request);
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return task.GetProperty("taskId").GetGuid();
    }

    private static async Task WaitForTerminalRunAsync(HttpClient client, Guid taskId, Guid runId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>(
                $"/api/tasks/{taskId}/translation-runs/{runId}");
            if (run.GetProperty("status").GetString() is "completed" or "partial_failed" or "failed")
            {
                Assert.Equal("completed", run.GetProperty("status").GetString());
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The translation run did not complete.");
    }

    private static HttpClient CreateClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);
        return client;
    }

    private sealed class MemoryComparisonTranslationClient : TestTranslationChatClient
    {
        protected override Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TestTranslationOutput>>(
            request.Segments.Select(segment => new TestTranslationOutput(
                segment.SegmentId,
                "使用翻译缓存。"))
            .ToArray());
    }
}

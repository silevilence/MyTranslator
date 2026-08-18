using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Tests;

public sealed class TermApiTests
{
    private const string DevelopmentToken = "sk-dev-00000000000000000000000000000000";

    [Fact]
    public async Task CreateAndReadTermPreservesNormalizedContractFields()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);

        var createResponse = await client.PostAsJsonAsync(
            "/api/terms",
            new
            {
                sourceTerm = "  translation memory  ",
                targetTerm = "  翻译记忆库  ",
                sourceLanguage = "EN",
                targetLanguage = "zh-cn",
                notes = "  固定译法  ",
                caseSensitive = false
            });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("translation memory", created.GetProperty("sourceTerm").GetString());
        Assert.Equal("翻译记忆库", created.GetProperty("targetTerm").GetString());
        Assert.Equal("en", created.GetProperty("sourceLanguage").GetString());
        Assert.Equal("zh-CN", created.GetProperty("targetLanguage").GetString());
        Assert.Equal("固定译法", created.GetProperty("notes").GetString());
        Assert.False(created.GetProperty("caseSensitive").GetBoolean());
        Assert.Equal(1, created.GetProperty("version").GetInt32());

        var termId = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/terms/{termId}", createResponse.Headers.Location?.OriginalString);

        var readResponse = await client.GetAsync($"/api/terms/{termId}");
        var read = await readResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        Assert.Equal(termId, read.GetProperty("id").GetGuid());
        Assert.Equal("translation memory", read.GetProperty("sourceTerm").GetString());
        Assert.Equal(1, read.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task UpdateAndDeleteTermRequireCurrentVersion()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var created = await CreateTermAsync(client, "API", "接口", caseSensitive: true);
        var termId = created.GetProperty("id").GetGuid();

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/terms/{termId}",
            new
            {
                sourceTerm = "API",
                targetTerm = "应用程序接口",
                sourceLanguage = "en",
                targetLanguage = "zh-CN",
                notes = "更新后的规定译法",
                caseSensitive = true,
                version = 1
            });
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal("应用程序接口", updated.GetProperty("targetTerm").GetString());
        Assert.Equal(2, updated.GetProperty("version").GetInt32());

        var staleDeleteResponse = await client.DeleteAsync($"/api/terms/{termId}?version=1");
        var staleProblem = await staleDeleteResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, staleDeleteResponse.StatusCode);
        Assert.Equal("term_version_conflict", staleProblem.GetProperty("code").GetString());
        Assert.Equal(1, staleProblem.GetProperty("errors").GetProperty("requestedVersion").GetInt32());
        Assert.Equal(2, staleProblem.GetProperty("errors").GetProperty("currentVersion").GetInt32());

        var deleteResponse = await client.DeleteAsync($"/api/terms/{termId}?version=2");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var missingResponse = await client.GetAsync($"/api/terms/{termId}");
        var missingProblem = await missingResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal("term_not_found", missingProblem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FuzzySearchFindsCompleteTermInsideLongerQuery()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var created = await CreateTermAsync(client, "translation memory", "翻译记忆库");
        _ = await CreateTermAsync(client, "glossary", "术语表");

        var page = await client.GetFromJsonAsync<JsonElement>(
            "/api/terms?query=using%20translation%20memory%20for%20consistency&sourceLanguage=en&targetLanguage=zh-CN");
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal(created.GetProperty("id").GetGuid(), item.GetProperty("id").GetGuid());
        Assert.Equal(0.8, item.GetProperty("matchScore").GetDouble());
        Assert.Equal("source", item.GetProperty("matchedField").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task FuzzySearchReturnsDocumentedLevenshteinScore()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        _ = await CreateTermAsync(client, "translation", "翻译");

        var page = await client.GetFromJsonAsync<JsonElement>("/api/terms?query=translatoin");
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal(0.8182, item.GetProperty("matchScore").GetDouble());
        Assert.Equal("source", item.GetProperty("matchedField").GetString());
    }

    [Fact]
    public async Task AlignmentCheckReturnsOnlySegmentsMissingExpectedTerms()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            translationProvider: new AlignmentTranslationClient());
        using var client = CreateClient(factory);
        _ = await CreateTermAsync(client, "translation memory", "翻译记忆库");
        _ = await CreateTermAsync(client, "Hello World", "你好 World");
        var taskId = await ImportMarkdownAsync(
            client,
            "Use **translation memory**.\n\nHello **World**.");

        var runResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var run = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Accepted, runResponse.StatusCode);
        await WaitForTerminalRunAsync(client, taskId, run.GetProperty("runId").GetGuid());

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/term-alignment-checks",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = result.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("totalSegments").GetInt32());
        Assert.Equal(2, summary.GetProperty("checkedSegments").GetInt32());
        Assert.Equal(2, summary.GetProperty("languagePairTermCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("matchedSegments").GetInt32());
        Assert.Equal(1, summary.GetProperty("alignedSegments").GetInt32());
        Assert.Equal(1, summary.GetProperty("unalignedSegments").GetInt32());
        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal(1, item.GetProperty("segmentOrder").GetInt32());
        Assert.Equal(2, item.GetProperty("segmentVersion").GetInt32());
        var misalignment = Assert.Single(item.GetProperty("misalignments").EnumerateArray());
        Assert.Equal("translation memory", misalignment.GetProperty("sourceTerm").GetString());
        Assert.Equal("翻译记忆库", misalignment.GetProperty("expectedTargetTerm").GetString());
        Assert.Equal(1, misalignment.GetProperty("sourceOccurrences").GetInt32());
        Assert.Equal(0, misalignment.GetProperty("targetOccurrences").GetInt32());
    }

    [Fact]
    public async Task TermListUsesLanguageFiltersAndBoundOpaqueCursor()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var first = await CreateTermAsync(client, "alpha", "甲");
        var second = await CreateTermAsync(client, "beta", "乙");
        _ = await CreateTermAsync(client, "alpha", "アルファ", targetLanguage: "ja");

        var firstPage = await client.GetFromJsonAsync<JsonElement>(
            "/api/terms?sourceLanguage=en&targetLanguage=zh-CN&limit=1");
        var firstItem = Assert.Single(firstPage.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, firstItem.GetProperty("matchScore").ValueKind);
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var secondPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/terms?sourceLanguage=en&targetLanguage=zh-CN&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        var secondItem = Assert.Single(secondPage.GetProperty("items").EnumerateArray());
        Assert.Equal(
            new[] { first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid() }.Order(),
            new[] { firstItem.GetProperty("id").GetGuid(), secondItem.GetProperty("id").GetGuid() }.Order());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);

        var reboundResponse = await client.GetAsync(
            $"/api/terms?sourceLanguage=en&targetLanguage=ja&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        var problem = await reboundResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, reboundResponse.StatusCode);
        Assert.Equal("invalid_cursor", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SourceTermUniquenessIgnoresCaseWithinLanguagePair()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var existing = await CreateTermAsync(client, "API", "接口", caseSensitive: true);

        var response = await client.PostAsJsonAsync(
            "/api/terms",
            new
            {
                sourceTerm = "api",
                targetTerm = "应用程序接口",
                sourceLanguage = "en",
                targetLanguage = "zh-CN",
                caseSensitive = false
            });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("term_conflict", problem.GetProperty("code").GetString());
        Assert.Equal(
            existing.GetProperty("id").GetGuid(),
            problem.GetProperty("errors").GetProperty("conflictingTermId").GetGuid());
    }

    [Fact]
    public async Task AlignmentCheckSkipsUntranslatedSegmentsAndRejectsStaleRevision()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        _ = await CreateTermAsync(client, "translation", "翻译");
        var taskId = await ImportMarkdownAsync(client, "translation");

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/term-alignment-checks",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, result.GetProperty("summary").GetProperty("checkedSegments").GetInt32());
        Assert.Equal(1, result.GetProperty("summary").GetProperty("skippedUntranslatedSegments").GetInt32());
        Assert.Empty(result.GetProperty("items").EnumerateArray());

        var staleResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/term-alignment-checks",
            new { extractionRevision = 2, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var problem = await staleResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Equal("extraction_revision_changed", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("currentRevision").GetInt32());
    }

    [Fact]
    public async Task AlignmentCheckRejectsNonNullWhitespaceTargetWithSegmentLocation()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var taskId = await ImportMarkdownAsync(client, "translation");
        Guid segmentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var segment = await database.TranslationSegments.SingleAsync(item => item.TaskId == taskId);
            segmentId = segment.Id;
            segment.TargetText = "   ";
            await database.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/term-alignment-checks",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_segment_state", problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors");
        Assert.Equal(1, errors.GetProperty("invalidSegmentCount").GetInt32());
        Assert.Equal(segmentId, errors.GetProperty("firstInvalidSegmentId").GetGuid());
        Assert.Equal(1, errors.GetProperty("firstInvalidSegmentOrder").GetInt32());
    }

    [Fact]
    public async Task AlignmentCheckPreservesLiteralPlaceholderTextNotRegisteredInMarkupTable()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        _ = await CreateTermAsync(client, "<x99>", "字面标记");
        var taskId = await ImportMarkdownAsync(client, "placeholder");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var segment = await database.TranslationSegments.SingleAsync(item => item.TaskId == taskId);
            segment.SourceText = "Keep <x99> literal.";
            segment.TargetText = "保留字面内容。";
            segment.MarkupTableJson = "[]";
            segment.Version++;
            await database.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/term-alignment-checks",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(result.GetProperty("items").EnumerateArray());
        var misalignment = Assert.Single(item.GetProperty("misalignments").EnumerateArray());
        Assert.Equal("<x99>", misalignment.GetProperty("sourceTerm").GetString());
    }

    [Fact]
    public async Task CaseSensitiveTermDoesNotMatchDifferentSourceCase()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            translationProvider: new AlignmentTranslationClient());
        using var client = CreateClient(factory);
        _ = await CreateTermAsync(client, "API", "接口", caseSensitive: true);
        var taskId = await ImportMarkdownAsync(client, "api");
        var runResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var run = await runResponse.Content.ReadFromJsonAsync<JsonElement>();
        await WaitForTerminalRunAsync(client, taskId, run.GetProperty("runId").GetGuid());

        var result = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/term-alignment-checks",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(0, body.GetProperty("summary").GetProperty("matchedSegments").GetInt32());
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task TermEndpointsValidateUnicodeLengthAndRequireAuthentication()
    {
        using var factory = new ApiFactory();
        using var anonymousClient = factory.CreateClient();
        var unauthorized = await anonymousClient.GetAsync("/api/terms");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var client = CreateClient(factory);
        var response = await client.PostAsJsonAsync(
            "/api/terms",
            new
            {
                sourceTerm = string.Concat(Enumerable.Repeat("😀", 501)),
                targetTerm = "过长",
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_source_term", problem.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> CreateTermAsync(
        HttpClient client,
        string sourceTerm,
        string targetTerm,
        bool caseSensitive = false,
        string sourceLanguage = "en",
        string targetLanguage = "zh-CN")
    {
        var response = await client.PostAsJsonAsync(
            "/api/terms",
            new
            {
                sourceTerm,
                targetTerm,
                sourceLanguage,
                targetLanguage,
                caseSensitive
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> ImportMarkdownAsync(HttpClient client, string text)
    {
        using var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        request.Add(file, "file", "terms.md");
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

    private sealed class AlignmentTranslationClient : TestTranslationChatClient
    {
        protected override Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TestTranslationOutput>>(
            request.Segments.Select(segment => new TestTranslationOutput(
                segment.SegmentId,
                segment.SourceText.Contains("translation memory", StringComparison.Ordinal)
                    ? "使用 <x1>翻译记忆</x1>。"
                    : segment.SourceText.Contains("<x1>", StringComparison.Ordinal)
                        ? "你好 <x1>World</x1>。"
                        : "接口"))
                .ToArray());
    }
}

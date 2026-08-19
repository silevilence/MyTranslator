using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public async Task TermListWithoutQueryUsesBoundedDatabasePagination()
    {
        var interceptor = new TermListQueryInterceptor();
        using var factory = new ApiFactory(
            "Development",
            null,
            databaseInterceptor: interceptor);
        using var client = CreateClient(factory);
        var older = await CreateTermAsync(client, "alpha", "甲");
        var newer = await CreateTermAsync(client, "beta", "乙");
        interceptor.Clear();

        var response = await client.GetAsync("/api/terms?limit=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var firstItem = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(newer.GetProperty("id").GetGuid(), firstItem.GetProperty("id").GetGuid());
        var command = Assert.Single(interceptor.Commands, sql =>
            sql.Contains("FROM \"Terms\"", StringComparison.Ordinal) &&
            sql.Contains("ORDER BY \"t\".\"UpdatedAtSortKey\" DESC", StringComparison.Ordinal));
        Assert.Contains("LIMIT", command, StringComparison.Ordinal);

        interceptor.Clear();
        var cursor = page.GetProperty("nextCursor").GetString();
        var nextResponse = await client.GetAsync(
            $"/api/terms?limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.OK, nextResponse.StatusCode);
        var nextPage = await nextResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondItem = Assert.Single(nextPage.GetProperty("items").EnumerateArray());
        Assert.Equal(older.GetProperty("id").GetGuid(), secondItem.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, nextPage.GetProperty("nextCursor").ValueKind);
        var nextCommand = Assert.Single(interceptor.Commands, sql =>
            sql.Contains("FROM \"Terms\"", StringComparison.Ordinal) &&
            sql.Contains("ORDER BY \"t\".\"UpdatedAtSortKey\" DESC", StringComparison.Ordinal));
        Assert.Contains("LIMIT", nextCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("CASE WHEN", nextCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigratedTermSortKeysPreserveCursorPosition()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var database = new AppDbContext(options);
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260818000000_AddTerms");

        var newestId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var olderId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var legacyOffset = TimeSpan.FromHours(8);
        var newestUpdatedAt = new DateTimeOffset(2026, 8, 19, 18, 0, 0, 500, legacyOffset);
        var olderUpdatedAt = new DateTimeOffset(2026, 8, 19, 17, 0, 0, 400, legacyOffset);
        await InsertLegacyTermAsync(database, newestId, "newest", newestUpdatedAt);
        await InsertLegacyTermAsync(database, olderId, "older", olderUpdatedAt);
        await migrator.MigrateAsync();

        var service = new MyTranslator.Api.Terms.TermService(database);
        var firstPage = await service.ListAsync(null, null, null, 1, null, CancellationToken.None);
        var firstItem = Assert.Single(firstPage.Items);
        var secondPage = await service.ListAsync(
            null,
            null,
            null,
            1,
            firstPage.NextCursor,
            CancellationToken.None);
        var secondItem = Assert.Single(secondPage.Items);

        Assert.Equal(newestId, firstItem.Id);
        Assert.Equal(olderId, secondItem.Id);
        Assert.Null(secondPage.NextCursor);
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

    private static Task InsertLegacyTermAsync(
        AppDbContext database,
        Guid id,
        string sourceTerm,
        DateTimeOffset updatedAt) => database.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO "Terms"
            ("Id", "SourceTerm", "SourceTermKey", "TargetTerm", "SourceLanguage",
             "TargetLanguage", "Notes", "CaseSensitive", "Version", "CreatedAt", "UpdatedAt")
        VALUES
            ({id}, {sourceTerm}, {sourceTerm.ToUpperInvariant()}, '目标术语', 'en',
             'zh-CN', NULL, 0, 1, {updatedAt}, {updatedAt});
        """);

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

    private sealed class TermListQueryInterceptor : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public void Clear() => Commands.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

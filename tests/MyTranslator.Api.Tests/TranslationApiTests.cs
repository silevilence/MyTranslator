using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using MyTranslator.Api.Data;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.Tests;

public sealed class TranslationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string DevelopmentToken = "sk-dev-00000000000000000000000000000000";

    [Fact]
    public async Task ConfiguredLlmIsRequiredBeforeCreatingTranslationRun()
    {
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello world");

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var task = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("llm_not_configured", problem.GetProperty("code").GetString());
        Assert.Equal("failed", task.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SuccessfulRunTranslatesSegmentsAndUpdatesPublicState()
    {
        var translations = new Dictionary<string, string>
        {
            ["Hello <x1>world</x1>."] = "你好 <x1>世界</x1>。"
        };
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new StaticTranslationProvider(translations));
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportMarkdownAsync(client, "Hello **world**.");

        var createResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("runId").GetGuid();
        var completed = await WaitForTerminalRunAsync(client, taskId, runId);
        var task = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(segments.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        Assert.Equal($"/api/tasks/{taskId}/translation-runs/{runId}", createResponse.Headers.Location?.OriginalString);
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal(100.0, completed.GetProperty("progress").GetProperty("percent").GetDouble());
        Assert.Equal("completed", task.GetProperty("status").GetString());
        Assert.Equal("你好 <x1>世界</x1>。", segment.GetProperty("targetText").GetString());
        Assert.Equal("translated", segment.GetProperty("confirmationStatus").GetString());
        Assert.Equal(2, segment.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task PartialFailurePreservesSuccessAndRetrySelectsOnlyMissingSegments()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new RecoveringTranslationProvider());
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "One\n\nTwo");

        var firstCreate = await CreateRunAsync(client, taskId);
        var firstRunId = firstCreate.GetProperty("runId").GetGuid();
        var firstRun = await WaitForTerminalRunAsync(client, taskId, firstRunId);
        var failures = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/translation-runs/{firstRunId}/failures");
        var firstSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var firstItems = firstSegments.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal("partial_failed", firstRun.GetProperty("status").GetString());
        Assert.Equal(1, firstRun.GetProperty("progress").GetProperty("succeededSegments").GetInt32());
        Assert.Equal(1, firstRun.GetProperty("progress").GetProperty("failedSegments").GetInt32());
        var failure = Assert.Single(failures.GetProperty("items").EnumerateArray());
        Assert.Equal("llm_response_invalid", failure.GetProperty("code").GetString());
        Assert.Equal(3, failure.GetProperty("attempts").GetInt32());
        Assert.Equal("一", firstItems[0].GetProperty("targetText").GetString());
        Assert.Equal(JsonValueKind.Null, firstItems[1].GetProperty("targetText").ValueKind);

        var secondCreate = await CreateRunAsync(client, taskId);
        var secondRunId = secondCreate.GetProperty("runId").GetGuid();
        var secondRun = await WaitForTerminalRunAsync(client, taskId, secondRunId);
        var finalSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var finalItems = finalSegments.GetProperty("items").EnumerateArray().ToArray();
        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/translation-runs");

        Assert.Equal(1, secondRun.GetProperty("selection").GetProperty("selectedSegments").GetInt32());
        Assert.Equal(1, secondRun.GetProperty("selection").GetProperty("skippedExistingSegments").GetInt32());
        Assert.Equal("completed", secondRun.GetProperty("status").GetString());
        Assert.Equal("一", finalItems[0].GetProperty("targetText").GetString());
        Assert.Equal("二", finalItems[1].GetProperty("targetText").GetString());
        Assert.Equal(2, runs.GetProperty("items").GetArrayLength());
        Assert.Equal(secondRunId, runs.GetProperty("items")[0].GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task UnavailableProviderFailsRunWithRetryablePublicFailure()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new FailingTranslationProvider(
                "llm_provider_unavailable",
                retryable: true));
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");

        var created = await CreateRunAsync(client, taskId);
        var runId = created.GetProperty("runId").GetGuid();
        var run = await WaitForTerminalRunAsync(client, taskId, runId);
        var task = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");
        var failurePage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/translation-runs/{runId}/failures");
        var failure = Assert.Single(failurePage.GetProperty("items").EnumerateArray());

        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Equal("llm_provider_unavailable", run.GetProperty("failure").GetProperty("code").GetString());
        Assert.True(run.GetProperty("failure").GetProperty("retryable").GetBoolean());
        Assert.Equal("failed", task.GetProperty("status").GetString());
        Assert.Equal("llm_provider_unavailable", failure.GetProperty("code").GetString());
        Assert.Equal(3, failure.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task PlaceholderCorruptionIsRejectedWithoutPersistingTargetText()
    {
        var translations = new Dictionary<string, string>
        {
            ["Hello <x1>world</x1>."] = "你好，世界。"
        };
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new StaticTranslationProvider(translations));
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportMarkdownAsync(client, "Hello **world**.");

        var created = await CreateRunAsync(client, taskId);
        var runId = created.GetProperty("runId").GetGuid();
        var run = await WaitForTerminalRunAsync(client, taskId, runId);
        var failures = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/translation-runs/{runId}/failures");
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var segment = Assert.Single(segments.GetProperty("items").EnumerateArray());

        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Equal(
            "placeholder_integrity_violation",
            Assert.Single(failures.GetProperty("items").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, segment.GetProperty("targetText").ValueKind);
        Assert.Equal("pending", segment.GetProperty("confirmationStatus").GetString());
        Assert.Equal(1, segment.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task ActiveRunRejectsDuplicateTriggerAndExport()
    {
        var provider = new BlockingTranslationProvider();
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: provider);
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");

        var created = await CreateRunAsync(client, taskId);
        var duplicateResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var duplicateProblem = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        var exportResponse = await client.GetAsync($"/api/tasks/{taskId}/export");
        var exportProblem = await exportResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        Assert.Equal("task_busy", duplicateProblem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, exportResponse.StatusCode);
        Assert.Equal("task_busy", exportProblem.GetProperty("code").GetString());

        provider.Release();
        var runId = created.GetProperty("runId").GetGuid();
        Assert.Equal("completed", (await WaitForTerminalRunAsync(client, taskId, runId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ActiveRunGetIncludesRetryAfterHeader()
    {
        var provider = new BlockingTranslationProvider();
        using var configuredFactory = new ApiFactory("Development", null, translationProvider: provider);
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");

        var created = await CreateRunAsync(client, taskId);
        var runId = created.GetProperty("runId").GetGuid();
        var response = await client.GetAsync($"/api/tasks/{taskId}/translation-runs/{runId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);

        provider.Release();
        await WaitForTerminalRunAsync(client, taskId, runId);
    }

    [Fact]
    public async Task DifferentTasksCanTranslateConcurrently()
    {
        var provider = new ConcurrentBlockingTranslationProvider();
        using var configuredFactory = new ApiFactory("Development", null, translationProvider: provider);
        using var client = CreateClient(configuredFactory);
        var firstTaskId = await ImportTextAsync(client, "One");
        var secondTaskId = await ImportTextAsync(client, "Two");
        var firstRun = await CreateRunAsync(client, firstTaskId);
        var secondRun = await CreateRunAsync(client, secondTaskId);

        try
        {
            await provider.WaitForConcurrentCallsAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            provider.Release();
        }

        Assert.Equal(
            "completed",
            (await WaitForTerminalRunAsync(client, firstTaskId, firstRun.GetProperty("runId").GetGuid()))
            .GetProperty("status").GetString());
        Assert.Equal(
            "completed",
            (await WaitForTerminalRunAsync(client, secondTaskId, secondRun.GetProperty("runId").GetGuid()))
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task StaleExtractionRevisionReturnsConflictWithoutCreatingRun()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new StaticTranslationProvider(
                new Dictionary<string, string> { ["Hello"] = "你好" }));
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 2, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/translation-runs");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("extraction_revision_changed", problem.GetProperty("code").GetString());
        Assert.Equal(1, problem.GetProperty("errors").GetProperty("currentRevision").GetInt32());
        Assert.Empty(runs.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ExtractionRevisionWithWrongJsonTypeReturnsStableProblemCode()
    {
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello");

        using var content = new StringContent(
            "{\"extractionRevision\":\"1\",\"sourceLanguage\":\"en\",\"targetLanguage\":\"zh-CN\"}",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync($"/api/tasks/{taskId}/translation-runs", content);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_extraction_revision", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidSegmentStateReturnsStableProblemCode()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new EchoTranslationProvider());
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");
        await UpdateSingleSegmentAsync(configuredFactory, taskId, "   ", SegmentConfirmationStatus.Pending);

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_segment_state", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConfirmedSegmentWithoutTargetReturnsInvalidSegmentState()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new EchoTranslationProvider());
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");
        await UpdateSingleSegmentAsync(configuredFactory, taskId, null, SegmentConfirmationStatus.Confirmed);

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_segment_state", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoSegmentsToTranslateReturnsConflictAndCompletesTask()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new EchoTranslationProvider());
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");
        await UpdateSingleSegmentAsync(configuredFactory, taskId, "你好", SegmentConfirmationStatus.Translated);

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var task = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_segments_to_translate", problem.GetProperty("code").GetString());
        Assert.Equal("completed", task.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SameNormalizedLanguagePairReturnsUnprocessableEntity()
    {
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello");

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "ZH-cn", targetLanguage = "zh-CN" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unsupported_language_pair", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TranslationRunListsRejectInvalidPaginationAndCursor()
    {
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello");

        var paginationResponse = await client.GetAsync($"/api/tasks/{taskId}/translation-runs?limit=0");
        var paginationProblem = await paginationResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cursorResponse = await client.GetAsync($"/api/tasks/{taskId}/translation-runs?cursor=not-a-cursor");
        var cursorProblem = await cursorResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, paginationResponse.StatusCode);
        Assert.Equal("invalid_pagination", paginationProblem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, cursorResponse.StatusCode);
        Assert.Equal("invalid_cursor", cursorProblem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("en-a")]
    [InlineData("en_US")]
    [InlineData("   ")]
    public async Task InvalidSourceLanguageTagsReturnStableProblemCode(string sourceLanguage)
    {
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello");

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage, targetLanguage = "zh-CN" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_language_tag", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("und")]
    [InlineData("sl-rozaj-biske")]
    public async Task WellFormedBcp47TagsAreNotRejectedByPlatformCultureData(string sourceLanguage)
    {
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello");

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage, targetLanguage = "zh-CN" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("llm_not_configured", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FailureListUsesOpaqueCursorPagination()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new FailingTranslationProvider(
                "llm_provider_unavailable",
                retryable: true));
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "One\n\nTwo");
        var created = await CreateRunAsync(client, taskId);
        var runId = created.GetProperty("runId").GetGuid();
        await WaitForTerminalRunAsync(client, taskId, runId);

        var firstPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/translation-runs/{runId}/failures?limit=1");
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        var secondPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/translation-runs/{runId}/failures?limit=1&cursor={Uri.EscapeDataString(cursor!)}");

        Assert.Equal(1, Assert.Single(firstPage.GetProperty("items").EnumerateArray()).GetProperty("segmentOrder").GetInt32());
        Assert.NotNull(cursor);
        Assert.Equal(2, Assert.Single(secondPage.GetProperty("items").EnumerateArray()).GetProperty("segmentOrder").GetInt32());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task ReextractionInvalidatesTranslationRunsFromOldRevision()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationProvider: new EchoTranslationProvider());
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportHtmlAsync(client, "<html><body><main><p>Hello</p></main></body></html>");
        var created = await CreateRunAsync(client, taskId);
        var runId = created.GetProperty("runId").GetGuid();
        Assert.Equal("completed", (await WaitForTerminalRunAsync(client, taskId, runId)).GetProperty("status").GetString());

        var previewResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews",
            new { selector = "main" });
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var previewId = preview.GetProperty("previewId").GetGuid();
        var applyResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews/{previewId}/apply",
            new { confirmTranslationLoss = true });
        var applied = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var oldRunResponse = await client.GetAsync($"/api/tasks/{taskId}/translation-runs/{runId}");
        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/translation-runs");

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);
        Assert.Equal(2, applied.GetProperty("extractionRevision").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, oldRunResponse.StatusCode);
        Assert.Empty(runs.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task RestartMarksActiveRunFailedAndKeepsCommittedStateQueryable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"mytranslator-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        Guid taskId;
        Guid runId;
        try
        {
            using (var firstFactory = new ApiFactory(
                       "Development",
                       null,
                       translationProvider: new BlockingTranslationProvider(),
                       connectionString: connectionString))
            using (var firstClient = CreateClient(firstFactory))
            {
                taskId = await ImportTextAsync(firstClient, "Hello");
                var created = await CreateRunAsync(firstClient, taskId);
                runId = created.GetProperty("runId").GetGuid();
            }

            using (var restartedFactory = new ApiFactory(
                       "Development",
                       null,
                       translationProvider: new EchoTranslationProvider(),
                       connectionString: connectionString))
            using (var restartedClient = CreateClient(restartedFactory))
            {
                var run = await restartedClient.GetFromJsonAsync<JsonElement>(
                    $"/api/tasks/{taskId}/translation-runs/{runId}");
                var task = await restartedClient.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");
                var failures = await restartedClient.GetFromJsonAsync<JsonElement>(
                    $"/api/tasks/{taskId}/translation-runs/{runId}/failures");

                Assert.Equal("failed", run.GetProperty("status").GetString());
                Assert.Equal("translation_interrupted", run.GetProperty("failure").GetProperty("code").GetString());
                Assert.True(run.GetProperty("failure").GetProperty("retryable").GetBoolean());
                Assert.Equal("failed", task.GetProperty("status").GetString());
                Assert.Equal(
                    "translation_interrupted",
                    Assert.Single(failures.GetProperty("items").EnumerateArray()).GetProperty("code").GetString());
            }
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task FileBackedDatabaseUsesWriteAheadLogging()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"mytranslator-wal-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            using var configuredFactory = new ApiFactory(
                "Development",
                null,
                translationProvider: new EchoTranslationProvider(),
                connectionString: connectionString);
            using var client = CreateClient(configuredFactory);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode";

            Assert.Equal("wal", Assert.IsType<string>(await command.ExecuteScalarAsync()));
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task ConfiguredHttpProviderCompletesRunThroughPublicApi()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationHandler: new ChatCompletionHandler());
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");

        var created = await CreateRunAsync(client, taskId);
        var runId = created.GetProperty("runId").GetGuid();
        var run = await WaitForTerminalRunAsync(client, taskId, runId);
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");

        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal(
            "译文：Hello",
            Assert.Single(segments.GetProperty("items").EnumerateArray()).GetProperty("targetText").GetString());
    }

    [Fact]
    public async Task InvalidHttpProviderStructureIsRetriedAsInvalidResponse()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationHandler: new EmptyChoicesHandler());
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");

        var created = await CreateRunAsync(client, taskId);
        var runId = created.GetProperty("runId").GetGuid();
        var run = await WaitForTerminalRunAsync(client, taskId, runId);
        var failures = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/translation-runs/{runId}/failures");
        var failure = Assert.Single(failures.GetProperty("items").EnumerateArray());

        Assert.Equal("failed", run.GetProperty("status").GetString());
        Assert.Equal("llm_response_invalid", run.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal("llm_response_invalid", failure.GetProperty("code").GetString());
        Assert.Equal(3, failure.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task ExplicitProviderLanguagePairRejectionUsesContractFailureCode()
    {
        using var configuredFactory = new ApiFactory(
            "Development",
            null,
            translationHandler: new UnsupportedLanguagePairHandler());
        using var client = CreateClient(configuredFactory);
        var taskId = await ImportTextAsync(client, "Hello");

        var created = await CreateRunAsync(client, taskId);
        var runId = created.GetProperty("runId").GetGuid();
        var run = await WaitForTerminalRunAsync(client, taskId, runId);
        var failures = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/translation-runs/{runId}/failures");
        var failure = Assert.Single(failures.GetProperty("items").EnumerateArray());

        Assert.Equal("unsupported_language_pair", run.GetProperty("failure").GetProperty("code").GetString());
        Assert.False(run.GetProperty("failure").GetProperty("retryable").GetBoolean());
        Assert.Equal("unsupported_language_pair", failure.GetProperty("code").GetString());
        Assert.Equal(1, failure.GetProperty("attempts").GetInt32());
    }

    private static HttpClient CreateClient(ApiFactory apiFactory)
    {
        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);
        return client;
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

    private static async Task<Guid> ImportMarkdownAsync(HttpClient client, string markdown)
    {
        using var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(markdown));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        request.Add(file, "file", "sample.md");
        request.Add(new StringContent("markdown"), "fileType");

        var response = await client.PostAsync("/api/tasks/imports/file", request);
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return task.GetProperty("taskId").GetGuid();
    }

    private static async Task<Guid> ImportHtmlAsync(HttpClient client, string html)
    {
        using var request = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        request.Add(file, "file", "sample.html");
        request.Add(new StringContent("html"), "fileType");

        var response = await client.PostAsync("/api/tasks/imports/file", request);
        var task = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return task.GetProperty("taskId").GetGuid();
    }

    private static async Task<JsonElement> CreateRunAsync(HttpClient client, Guid taskId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return run;
    }

    private static async Task UpdateSingleSegmentAsync(
        ApiFactory apiFactory,
        Guid taskId,
        string? targetText,
        SegmentConfirmationStatus confirmationStatus)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var segment = await database.TranslationSegments.SingleAsync(item => item.TaskId == taskId);
        segment.TargetText = targetText;
        segment.ConfirmationStatus = confirmationStatus;
        await database.SaveChangesAsync();
    }

    private static async Task<JsonElement> WaitForTerminalRunAsync(
        HttpClient client,
        Guid taskId,
        Guid runId)
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

    private sealed class StaticTranslationProvider(
        IReadOnlyDictionary<string, string> translations) : TestTranslationChatClient
    {
        protected override Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TestTranslationOutput>>(
                request.Segments
                    .Select(segment => new TestTranslationOutput(
                        segment.SegmentId,
                        translations[segment.SourceText]))
                    .ToArray());
    }

    private sealed class RecoveringTranslationProvider : TestTranslationChatClient
    {
        private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);

        protected override Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TestTranslationOutput>>(
                request.Segments.Select(segment =>
                {
                    var attempt = attempts.AddOrUpdate(segment.SourceText, 1, (_, current) => current + 1);
                    return new TestTranslationOutput(
                        segment.SegmentId,
                        segment.SourceText switch
                        {
                            "One" => "一",
                            "Two" when attempt <= 3 => string.Empty,
                            "Two" => "二",
                            _ => throw new InvalidOperationException("Unexpected test segment.")
                        });
                }).ToArray());
    }

    private sealed class FailingTranslationProvider(string code, bool retryable) : TestTranslationChatClient
    {
        protected override Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken) => throw new TranslationExecutionException(
                code,
                retryable,
                "Configured test failure.");
    }

    private sealed class BlockingTranslationProvider : TestTranslationChatClient
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken)
        {
            await released.Task.WaitAsync(cancellationToken);
            return request.Segments
                .Select(segment => new TestTranslationOutput(segment.SegmentId, $"译文：{segment.SourceText}"))
                .ToArray();
        }

        public void Release() => released.TrySetResult();
    }

    private sealed class ConcurrentBlockingTranslationProvider : TestTranslationChatClient
    {
        private readonly TaskCompletionSource concurrentCalls = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        protected override async Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) >= 2)
            {
                concurrentCalls.TrySetResult();
            }

            await released.Task.WaitAsync(cancellationToken);
            return request.Segments
                .Select(segment => new TestTranslationOutput(segment.SegmentId, $"译文：{segment.SourceText}"))
                .ToArray();
        }

        public Task WaitForConcurrentCallsAsync(TimeSpan timeout) => concurrentCalls.Task.WaitAsync(timeout);

        public void Release() => released.TrySetResult();
    }

    private sealed class EchoTranslationProvider : TestTranslationChatClient
    {
        protected override Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TestTranslationOutput>>(
                request.Segments
                    .Select(segment => new TestTranslationOutput(segment.SegmentId, $"译文：{segment.SourceText}"))
                    .ToArray());
    }

    private abstract class TestTranslationChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var userMessage = messages.Last(message => message.Role == ChatRole.User).Text;
            using var document = JsonDocument.Parse(userMessage);
            var request = new TestTranslationRequest(
                document.RootElement.TryGetProperty("sourceLanguage", out var source) &&
                source.ValueKind == JsonValueKind.String
                    ? source.GetString()
                    : null,
                document.RootElement.GetProperty("targetLanguage").GetString()!,
                document.RootElement.GetProperty("segments").EnumerateArray()
                    .Select(segment => new TestTranslationSegment(
                        segment.GetProperty("segmentId").GetGuid(),
                        segment.GetProperty("sourceText").GetString()!))
                    .ToArray());
            var outputs = await TranslateCoreAsync(request, cancellationToken);
            var content = JsonSerializer.Serialize(new
            {
                translations = outputs.Select(output => new
                {
                    segmentId = output.SegmentId,
                    targetText = output.TargetText
                })
            });
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new NotSupportedException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        protected abstract Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
            TestTranslationRequest request,
            CancellationToken cancellationToken);
    }

    private sealed record TestTranslationRequest(
        string? SourceLanguage,
        string TargetLanguage,
        IReadOnlyList<TestTranslationSegment> Segments);

    private sealed record TestTranslationSegment(Guid SegmentId, string SourceText);

    private sealed record TestTranslationOutput(Guid SegmentId, string TargetText);

    private sealed class ChatCompletionHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
            using var input = JsonDocument.Parse(
                body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var translations = input.RootElement.GetProperty("segments").EnumerateArray()
                .Select(segment => new
                {
                    segmentId = segment.GetProperty("segmentId").GetGuid(),
                    targetText = $"译文：{segment.GetProperty("sourceText").GetString()}"
                })
                .ToArray();
            var content = JsonSerializer.Serialize(new { translations });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[]
                    {
                        new { message = new { content } }
                    }
                })
            };
        }
    }

    private sealed class EmptyChoicesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { choices = Array.Empty<object>() })
            });
    }

    private sealed class UnsupportedLanguagePairHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new
                {
                    error = new { code = "unsupported_language_pair" }
                })
            });
    }
}

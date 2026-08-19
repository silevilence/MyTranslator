using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MyTranslator.Api.Tests;

public sealed class ReviewApiTests
{
    private const string DevelopmentToken = "sk-dev-00000000000000000000000000000000";

    [Fact]
    public async Task ReviewRunRequiresTranslatedSegmentWithoutChangingTaskStatus()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello world");

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var task = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_segments_to_review", problem.GetProperty("code").GetString());
        Assert.Equal("created", task.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreatedReviewRunIsQueuedAndQueryableWithoutChangingTaskStatus()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            translationProvider: new ReviewTestChatClient());
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello world");
        await TranslateTaskAsync(client, taskId);
        var taskBeforeReview = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("runId").GetGuid();
        var detail = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/review-runs/{runId}");
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/review-runs");
        var taskAfterReview = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/tasks/{taskId}/review-runs/{runId}", response.Headers.Location?.OriginalString);
        Assert.Equal("queued", created.GetProperty("status").GetString());
        Assert.Equal(1, created.GetProperty("selection").GetProperty("totalSegments").GetInt32());
        Assert.Equal(1, created.GetProperty("selection").GetProperty("selectedSegments").GetInt32());
        Assert.Equal(0, created.GetProperty("selection").GetProperty("skippedUntranslatedSegments").GetInt32());
        Assert.Equal(0.0, created.GetProperty("progress").GetProperty("percent").GetDouble());
        Assert.NotEqual(Guid.Empty, created.GetProperty("providerId").GetGuid());
        Assert.NotEqual(Guid.Empty, created.GetProperty("modelId").GetGuid());
        Assert.Equal(runId, detail.GetProperty("runId").GetGuid());
        Assert.Equal(runId, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("runId").GetGuid());
        Assert.Equal("completed", taskBeforeReview.GetProperty("status").GetString());
        Assert.Equal("completed", taskAfterReview.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CompletedReviewRunPublishesCommentsOnSegments()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            translationProvider: new ReviewTestChatClient());
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello world");
        await TranslateTaskAsync(client, taskId);

        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var run = await WaitForReviewRunAsync(client, taskId, created.GetProperty("runId").GetGuid());
        var segments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var comment = Assert.Single(
            Assert.Single(segments.GetProperty("items").EnumerateArray())
                .GetProperty("reviewComments")
                .EnumerateArray());

        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Equal(100.0, run.GetProperty("progress").GetProperty("percent").GetDouble());
        Assert.Equal("high", comment.GetProperty("severity").GetString());
        Assert.Equal("Meaning is too broad.", comment.GetProperty("issue").GetString());
        Assert.Equal("Use a more precise term.", comment.GetProperty("suggestion").GetString());
        Assert.False(comment.TryGetProperty("id", out _));
        Assert.False(comment.TryGetProperty("runId", out _));
    }

    [Fact]
    public async Task FailedRereviewRetainsPreviousCommentsAndReportsRetryAttempts()
    {
        var chatClient = new ReviewTestChatClient((reviewCall, segments) =>
        {
            var segmentId = segments[0].GetProperty("segmentId").GetGuid();
            var comments = reviewCall == 1
                ? new[] { new { severity = "unexpected", issue = "Keep this issue.", suggestion = (string?)null } }
                : Enumerable.Range(0, 21)
                    .Select(index => new
                    {
                        severity = "low",
                        issue = $"Too many comments {index}.",
                        suggestion = (string?)null
                    })
                    .ToArray();
            return JsonSerializer.Serialize(new
            {
                reviews = new[] { new { segmentId, comments } }
            });
        });
        using var factory = new ApiFactory("Development", null, translationProvider: chatClient);
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello world");
        await TranslateTaskAsync(client, taskId);

        var firstRun = await CreateAndWaitForReviewRunAsync(client, taskId);
        var firstSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var firstComment = Assert.Single(
            Assert.Single(firstSegments.GetProperty("items").EnumerateArray())
                .GetProperty("reviewComments")
                .EnumerateArray());
        var secondRun = await CreateAndWaitForReviewRunAsync(client, taskId);
        var failures = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks/{taskId}/review-runs/{secondRun.GetProperty("runId").GetGuid()}/failures");
        var retainedSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var retainedComment = Assert.Single(
            Assert.Single(retainedSegments.GetProperty("items").EnumerateArray())
                .GetProperty("reviewComments")
                .EnumerateArray());

        Assert.Equal("completed", firstRun.GetProperty("status").GetString());
        Assert.Equal("medium", firstComment.GetProperty("severity").GetString());
        Assert.Equal("failed", secondRun.GetProperty("status").GetString());
        Assert.Equal("llm_response_invalid", secondRun.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal(3, Assert.Single(failures.GetProperty("items").EnumerateArray()).GetProperty("attempts").GetInt32());
        Assert.Equal("Keep this issue.", retainedComment.GetProperty("issue").GetString());
    }

    [Fact]
    public async Task SuccessfulRereviewSortsAndCanClearComments()
    {
        var chatClient = new ReviewTestChatClient((reviewCall, segments) =>
        {
            var segmentId = segments[0].GetProperty("segmentId").GetGuid();
            var comments = reviewCall == 1
                ? new[]
                {
                    new { severity = "low", issue = "Low issue", suggestion = (string?)null },
                    new { severity = "high", issue = "High issue", suggestion = (string?)null },
                    new { severity = "unknown", issue = "Medium issue", suggestion = (string?)"   " }
                }
                : [];
            return JsonSerializer.Serialize(new
            {
                reviews = new[] { new { segmentId, comments } }
            });
        });
        using var factory = new ApiFactory("Development", null, translationProvider: chatClient);
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello world");
        await TranslateTaskAsync(client, taskId);

        await CreateAndWaitForReviewRunAsync(client, taskId);
        var firstSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");
        var comments = Assert.Single(firstSegments.GetProperty("items").EnumerateArray())
            .GetProperty("reviewComments")
            .EnumerateArray()
            .ToArray();
        await CreateAndWaitForReviewRunAsync(client, taskId);
        var secondSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");

        Assert.Equal(new[] { "high", "medium", "low" },
            comments.Select(comment => comment.GetProperty("severity").GetString()));
        Assert.Equal(JsonValueKind.Null, comments[1].GetProperty("suggestion").ValueKind);
        Assert.Empty(
            Assert.Single(secondSegments.GetProperty("items").EnumerateArray())
                .GetProperty("reviewComments")
                .EnumerateArray());
    }

    [Fact]
    public async Task ReviewInputInjectsMatchingTermsAndKeepsMarkupOpaque()
    {
        var chatClient = new ReviewTestChatClient();
        using var factory = new ApiFactory("Development", null, translationProvider: chatClient);
        using var client = CreateClient(factory);
        var termResponse = await client.PostAsJsonAsync(
            "/api/terms",
            new
            {
                sourceTerm = "translation memory",
                targetTerm = "翻译记忆库",
                sourceLanguage = "en",
                targetLanguage = "zh-CN",
                caseSensitive = false
            });
        termResponse.EnsureSuccessStatusCode();
        var taskId = await ImportHtmlAsync(client, "<p>Use <strong>translation memory</strong>.</p>");
        await TranslateTaskAsync(client, taskId);

        await CreateAndWaitForReviewRunAsync(client, taskId);
        using var request = JsonDocument.Parse(chatClient.LastReviewRequest!);
        var segment = Assert.Single(request.RootElement.GetProperty("segments").EnumerateArray());
        var term = Assert.Single(segment.GetProperty("terms").EnumerateArray());

        Assert.Contains("<x", segment.GetProperty("sourceText").GetString());
        Assert.Equal("translation memory", term.GetProperty("sourceTerm").GetString());
        Assert.Equal("翻译记忆库", term.GetProperty("targetTerm").GetString());
        Assert.DoesNotContain("<strong>", chatClient.LastReviewRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("markupTable", chatClient.LastReviewRequest, StringComparison.Ordinal);
        Assert.False(request.RootElement.TryGetProperty("translationMemory", out _));
        Assert.Contains("20", chatClient.LastReviewSystemPrompt, StringComparison.Ordinal);

        var withoutSourceResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new { extractionRevision = 1, sourceLanguage = (string?)null, targetLanguage = "zh-CN" });
        withoutSourceResponse.EnsureSuccessStatusCode();
        var withoutSource = await withoutSourceResponse.Content.ReadFromJsonAsync<JsonElement>();
        await WaitForReviewRunAsync(client, taskId, withoutSource.GetProperty("runId").GetGuid());
        using var withoutSourceRequest = JsonDocument.Parse(chatClient.LastReviewRequest!);
        Assert.Empty(
            Assert.Single(withoutSourceRequest.RootElement.GetProperty("segments").EnumerateArray())
                .GetProperty("terms")
                .EnumerateArray());
    }

    [Fact]
    public async Task ActiveReviewRunBlocksConflictingTaskOperations()
    {
        var chatClient = new ReviewTestChatClient(blockReviews: true);
        using var factory = new ApiFactory("Development", null, translationProvider: chatClient);
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello world");
        await TranslateTaskAsync(client, taskId);

        var firstReview = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        firstReview.EnsureSuccessStatusCode();
        var created = await firstReview.Content.ReadFromJsonAsync<JsonElement>();
        await chatClient.ReviewStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var secondReview = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var translation = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var export = await client.GetAsync($"/api/tasks/{taskId}/export");

        Assert.Equal("task_busy", (await secondReview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("task_busy", (await translation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("task_busy", (await export.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        chatClient.ReleaseReview();
        await WaitForReviewRunAsync(client, taskId, created.GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task ReextractionDeletesReviewRunsAndCommentsFromOldRevision()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            translationProvider: new ReviewTestChatClient());
        using var client = CreateClient(factory);
        var taskId = await ImportHtmlAsync(client, "<html><body><main><p>Hello</p></main></body></html>");
        await TranslateTaskAsync(client, taskId);
        var run = await CreateAndWaitForReviewRunAsync(client, taskId);
        var runId = run.GetProperty("runId").GetGuid();

        var previewResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews",
            new { selector = "main" });
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var applyResponse = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/extraction-previews/{preview.GetProperty("previewId").GetGuid()}/apply",
            new { confirmTranslationLoss = true });
        var applied = await applyResponse.Content.ReadFromJsonAsync<JsonElement>();
        var oldRun = await client.GetAsync($"/api/tasks/{taskId}/review-runs/{runId}");
        var currentSegments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/segments");

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);
        Assert.Equal(2, applied.GetProperty("extractionRevision").GetInt32());
        Assert.Equal("created", applied.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, oldRun.StatusCode);
        Assert.Empty(
            Assert.Single(currentSegments.GetProperty("items").EnumerateArray())
                .GetProperty("reviewComments")
                .EnumerateArray());
    }

    [Fact]
    public async Task RestartMarksActiveReviewFailedWithoutChangingTaskStatus()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"mytranslator-review-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        Guid taskId;
        Guid runId;
        try
        {
            var blockingClient = new ReviewTestChatClient(blockReviews: true);
            using (var firstFactory = new ApiFactory(
                       "Development",
                       null,
                       translationProvider: blockingClient,
                       connectionString: connectionString))
            using (var firstClient = CreateClient(firstFactory))
            {
                taskId = await ImportTextAsync(firstClient, "Hello world");
                await TranslateTaskAsync(firstClient, taskId);
                var response = await firstClient.PostAsJsonAsync(
                    $"/api/tasks/{taskId}/review-runs",
                    new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
                response.EnsureSuccessStatusCode();
                var created = await response.Content.ReadFromJsonAsync<JsonElement>();
                runId = created.GetProperty("runId").GetGuid();
                await blockingClient.ReviewStarted.WaitAsync(TimeSpan.FromSeconds(5));
            }

            using var secondFactory = new ApiFactory(
                "Development",
                null,
                translationProvider: new ReviewTestChatClient(),
                connectionString: connectionString,
                seedTranslationConfiguration: false);
            using var secondClient = CreateClient(secondFactory);
            var run = await secondClient.GetFromJsonAsync<JsonElement>(
                $"/api/tasks/{taskId}/review-runs/{runId}");
            var task = await secondClient.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");
            var failures = await secondClient.GetFromJsonAsync<JsonElement>(
                $"/api/tasks/{taskId}/review-runs/{runId}/failures");

            Assert.Equal("failed", run.GetProperty("status").GetString());
            Assert.Equal("review_interrupted", run.GetProperty("failure").GetProperty("code").GetString());
            Assert.Equal("completed", task.GetProperty("status").GetString());
            Assert.Equal(
                "review_interrupted",
                Assert.Single(failures.GetProperty("items").EnumerateArray()).GetProperty("code").GetString());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ReviewConfigurationErrorsDoNotChangeTaskStatus()
    {
        using var factory = new ApiFactory(
            "Development",
            null,
            translationProvider: new ReviewTestChatClient());
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Hello world");
        await TranslateTaskAsync(client, taskId);
        var providers = await client.GetFromJsonAsync<JsonElement[]>("/api/providers");
        var providerId = Assert.Single(providers!).GetProperty("id").GetGuid();
        var modelId = Assert.Single(Assert.Single(providers!).GetProperty("models").EnumerateArray())
            .GetProperty("id")
            .GetGuid();

        var invalidSelection = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN",
                modelId
            });
        Assert.Equal("invalid_model_selection",
            (await invalidSelection.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var deleteProvider = await client.DeleteAsync($"/api/providers/{providerId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteProvider.StatusCode);
        var notConfigured = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new { extractionRevision = 1, sourceLanguage = "en", targetLanguage = "zh-CN" });
        var task = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, notConfigured.StatusCode);
        Assert.Equal("llm_not_configured",
            (await notConfigured.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("completed", task.GetProperty("status").GetString());
    }

    private static HttpClient CreateClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DevelopmentToken);
        return client;
    }

    private static async Task<Guid> ImportTextAsync(HttpClient client, string content)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "review.txt");
        form.Add(new StringContent("txt"), "fileType");

        var response = await client.PostAsync("/api/tasks/imports/file", form);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("taskId").GetGuid();
    }

    private static async Task<Guid> ImportHtmlAsync(HttpClient client, string content)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        form.Add(file, "file", "review.html");
        form.Add(new StringContent("html"), "fileType");

        var response = await client.PostAsync("/api/tasks/imports/file", form);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("taskId").GetGuid();
    }

    private static async Task TranslateTaskAsync(HttpClient client, Guid taskId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/translation-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        await WaitForTranslationRunAsync(client, taskId, created.GetProperty("runId").GetGuid());
    }

    private static async Task WaitForTranslationRunAsync(HttpClient client, Guid taskId, Guid runId)
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

            await Task.Delay(25);
        }

        throw new TimeoutException("The translation run did not complete.");
    }

    private static async Task<JsonElement> WaitForReviewRunAsync(HttpClient client, Guid taskId, Guid runId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/review-runs/{runId}");
            if (run.GetProperty("status").GetString() is "completed" or "partial_failed" or "failed")
            {
                return run;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The review run did not complete.");
    }

    private static async Task<JsonElement> CreateAndWaitForReviewRunAsync(HttpClient client, Guid taskId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/review-runs",
            new
            {
                extractionRevision = 1,
                sourceLanguage = "en",
                targetLanguage = "zh-CN"
            });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return await WaitForReviewRunAsync(client, taskId, created.GetProperty("runId").GetGuid());
    }

    private sealed class ReviewTestChatClient : IChatClient
    {
        private readonly Func<int, JsonElement[], string>? reviewResponseFactory;
        private int reviewCalls;

        public string? LastReviewRequest { get; private set; }
        public string? LastReviewSystemPrompt { get; private set; }

        private readonly bool blockReviews;
        private readonly TaskCompletionSource reviewStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseReview = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReviewTestChatClient(
            Func<int, JsonElement[], string>? reviewResponseFactory = null,
            bool blockReviews = false)
        {
            this.reviewResponseFactory = reviewResponseFactory;
            this.blockReviews = blockReviews;
        }

        public Task ReviewStarted => reviewStarted.Task;

        public void ReleaseReview() => releaseReview.TrySetResult();

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var userMessage = messages.Last(message => message.Role == ChatRole.User).Text;
            using var request = JsonDocument.Parse(userMessage);
            var segments = request.RootElement.GetProperty("segments").EnumerateArray().ToArray();
            var isReview = segments.Length > 0 && segments[0].TryGetProperty("targetText", out _);
            if (isReview)
            {
                LastReviewRequest = userMessage;
                LastReviewSystemPrompt = messages.Last(message => message.Role == ChatRole.System).Text;
                reviewStarted.TrySetResult();
                if (blockReviews)
                {
                    await releaseReview.Task.WaitAsync(cancellationToken);
                }
            }
            var content = isReview
                ? reviewResponseFactory?.Invoke(Interlocked.Increment(ref reviewCalls), segments) ??
                  JsonSerializer.Serialize(new
                  {
                      reviews = segments.Select(segment => new
                      {
                          segmentId = segment.GetProperty("segmentId").GetGuid(),
                          comments = new[]
                          {
                              new
                              {
                                  severity = "high",
                                  issue = "Meaning is too broad.",
                                  suggestion = "Use a more precise term."
                              }
                          }
                      })
                  })
                : JsonSerializer.Serialize(new
                {
                    translations = segments.Select(segment => new
                    {
                        segmentId = segment.GetProperty("segmentId").GetGuid(),
                        targetText = segment.GetProperty("sourceText").GetString()
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
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Tests;

public sealed class TaskListApiTests
{
    private const string DevelopmentToken = "sk-dev-00000000000000000000000000000000";

    [Fact]
    public async Task EmptyTaskListReturnsEmptyPage()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/tasks");
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task TaskListReturnsNewestTasksWithCurrentSegmentProgress()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var olderTaskId = await ImportTextAsync(client, "First\n\nSecond");
        var newerTaskId = await ImportTextAsync(client, "Only segment");
        var olderCreatedAt = new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero);
        var newerCreatedAt = olderCreatedAt.AddHours(1);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var olderTask = await database.TranslationTasks.SingleAsync(task => task.Id == olderTaskId);
            var newerTask = await database.TranslationTasks.SingleAsync(task => task.Id == newerTaskId);
            var translatedSegment = await database.TranslationSegments
                .Where(segment => segment.TaskId == olderTaskId)
                .OrderBy(segment => segment.Order)
                .FirstAsync();
            olderTask.CreatedAt = olderCreatedAt;
            newerTask.CreatedAt = newerCreatedAt;
            translatedSegment.TargetText = "译文";
            translatedSegment.ConfirmationStatus = SegmentConfirmationStatus.Translated;
            await database.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/tasks");
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Collection(
            page.GetProperty("items").EnumerateArray(),
            newer =>
            {
                Assert.Equal(newerTaskId, newer.GetProperty("taskId").GetGuid());
                Assert.Equal("created", newer.GetProperty("status").GetString());
                Assert.Equal("sample.txt", newer.GetProperty("source").GetProperty("fileName").GetString());
                Assert.Equal("txt", newer.GetProperty("fileType").GetString());
                Assert.Equal(1, newer.GetProperty("extractionRevision").GetInt32());
                Assert.Equal(0, newer.GetProperty("progress").GetProperty("completedSegments").GetInt32());
                Assert.Equal(1, newer.GetProperty("progress").GetProperty("totalSegments").GetInt32());
                Assert.Equal(JsonValueKind.Null, newer.GetProperty("latestTranslationRun").ValueKind);
                Assert.Equal(newerCreatedAt, newer.GetProperty("createdAt").GetDateTimeOffset());
            },
            older =>
            {
                Assert.Equal(olderTaskId, older.GetProperty("taskId").GetGuid());
                Assert.Equal(1, older.GetProperty("progress").GetProperty("completedSegments").GetInt32());
                Assert.Equal(2, older.GetProperty("progress").GetProperty("totalSegments").GetInt32());
            });
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task TaskListIncludesLatestTranslationRunSummary()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "One\n\nTwo\n\nThree\n\nFour");
        var latestRunId = Guid.NewGuid();
        var latestCreatedAt = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
        var latestFinishedAt = latestCreatedAt.AddMinutes(1);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.TranslationRuns.AddRange(
                new TranslationRun
                {
                    Id = Guid.NewGuid(),
                    TaskId = taskId,
                    ExtractionRevision = 1,
                    Status = TranslationRunStatus.Completed,
                    TargetLanguage = "zh-CN",
                    TotalSegments = 4,
                    SelectedSegments = 4,
                    ProcessedSegments = 4,
                    SucceededSegments = 4,
                    CreatedAt = latestCreatedAt.AddMinutes(-5),
                    StartedAt = latestCreatedAt.AddMinutes(-5),
                    FinishedAt = latestCreatedAt.AddMinutes(-4)
                },
                new TranslationRun
                {
                    Id = latestRunId,
                    TaskId = taskId,
                    ExtractionRevision = 1,
                    Status = TranslationRunStatus.PartialFailed,
                    TargetLanguage = "zh-CN",
                    TotalSegments = 4,
                    SelectedSegments = 4,
                    ProcessedSegments = 2,
                    SucceededSegments = 1,
                    FailedSegments = 1,
                    FailureCode = "segment_translation_failed",
                    FailureRetryable = true,
                    CreatedAt = latestCreatedAt,
                    StartedAt = latestCreatedAt,
                    FinishedAt = latestFinishedAt
                });
            await database.SaveChangesAsync();
        }

        var page = await client.GetFromJsonAsync<JsonElement>("/api/tasks");
        var latestRun = page.GetProperty("items")[0].GetProperty("latestTranslationRun");

        Assert.Equal(latestRunId, latestRun.GetProperty("runId").GetGuid());
        Assert.Equal("partial_failed", latestRun.GetProperty("status").GetString());
        Assert.Equal(2, latestRun.GetProperty("progress").GetProperty("processedSegments").GetInt32());
        Assert.Equal(1, latestRun.GetProperty("progress").GetProperty("succeededSegments").GetInt32());
        Assert.Equal(1, latestRun.GetProperty("progress").GetProperty("failedSegments").GetInt32());
        Assert.Equal(50.0, latestRun.GetProperty("progress").GetProperty("percent").GetDouble());
        Assert.Equal("segment_translation_failed", latestRun.GetProperty("failure").GetProperty("code").GetString());
        Assert.True(latestRun.GetProperty("failure").GetProperty("retryable").GetBoolean());
        Assert.Equal(1, latestRun.GetProperty("failure").GetProperty("failedSegments").GetInt32());
        Assert.Equal(latestCreatedAt, latestRun.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(latestFinishedAt, latestRun.GetProperty("finishedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task TaskListFiltersBySingleTaskStatus()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var completedTaskId = await ImportTextAsync(client, "Completed");
        var failedTaskId = await ImportTextAsync(client, "Failed");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var completedTask = await database.TranslationTasks.SingleAsync(task => task.Id == completedTaskId);
            var failedTask = await database.TranslationTasks.SingleAsync(task => task.Id == failedTaskId);
            completedTask.Status = TranslationTaskStatus.Completed;
            failedTask.Status = TranslationTaskStatus.Failed;
            await database.SaveChangesAsync();
        }

        var page = await client.GetFromJsonAsync<JsonElement>("/api/tasks?status=failed");
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal(failedTaskId, item.GetProperty("taskId").GetGuid());
        Assert.Equal("failed", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvalidTaskStatusReturnsStableProblemCode()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/tasks?status=unknown");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_task_status", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TaskListCursorKeepsStablePositionWhenNewTaskIsCreated()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var oldestTaskId = await ImportTextAsync(client, "Oldest");
        var middleTaskId = await ImportTextAsync(client, "Middle");
        var newestTaskId = await ImportTextAsync(client, "Newest");
        var baseTime = new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await database.TranslationTasks.SingleAsync(task => task.Id == oldestTaskId)).CreatedAt = baseTime;
            (await database.TranslationTasks.SingleAsync(task => task.Id == middleTaskId)).CreatedAt = baseTime.AddHours(1);
            (await database.TranslationTasks.SingleAsync(task => task.Id == newestTaskId)).CreatedAt = baseTime.AddHours(2);
            await database.SaveChangesAsync();
        }

        var firstPage = await client.GetFromJsonAsync<JsonElement>("/api/tasks?limit=2");
        Assert.Equal(
            [newestTaskId, middleTaskId],
            firstPage.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("taskId").GetGuid())
                .ToArray());
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        _ = await ImportTextAsync(client, "Created after the first page");
        var secondPage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tasks?limit=2&cursor={Uri.EscapeDataString(cursor!)}");

        var onlyItem = Assert.Single(secondPage.GetProperty("items").EnumerateArray());
        Assert.Equal(oldestTaskId, onlyItem.GetProperty("taskId").GetGuid());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task TaskListRejectsInvalidPaginationAndCursor()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);

        var invalidLimitResponse = await client.GetAsync("/api/tasks?limit=0");
        var invalidLimitProblem = await invalidLimitResponse.Content.ReadFromJsonAsync<JsonElement>();
        var invalidCursorResponse = await client.GetAsync("/api/tasks?cursor=not-a-cursor");
        var invalidCursorProblem = await invalidCursorResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, invalidLimitResponse.StatusCode);
        Assert.Equal("invalid_pagination", invalidLimitProblem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalidCursorResponse.StatusCode);
        Assert.Equal("invalid_cursor", invalidCursorProblem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TaskListCursorIsBoundToStatusFilter()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        _ = await ImportTextAsync(client, "First");
        _ = await ImportTextAsync(client, "Second");
        var firstPage = await client.GetFromJsonAsync<JsonElement>("/api/tasks?status=created&limit=1");
        var cursor = firstPage.GetProperty("nextCursor").GetString();

        var response = await client.GetAsync(
            $"/api/tasks?status=failed&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_cursor", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TaskListRequiresBearerToken()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/tasks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TaskListUsesOneConsistentSnapshotForTaskAndLatestRun()
    {
        var interceptor = new PauseBeforeRunQueryInterceptor();
        using var factory = new ApiFactory("Development", null, databaseInterceptor: interceptor);
        using var client = CreateClient(factory);
        var taskId = await ImportTextAsync(client, "Snapshot");
        interceptor.Arm();

        var listTask = client.GetAsync("/api/tasks");
        await interceptor.RunQueryReached.WaitAsync(TimeSpan.FromSeconds(5));
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentWrite = Task.Run(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var task = await database.TranslationTasks.SingleAsync(entity => entity.Id == taskId);
            task.Status = TranslationTaskStatus.Processing;
            database.TranslationRuns.Add(new TranslationRun
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                ExtractionRevision = 1,
                Status = TranslationRunStatus.Queued,
                TargetLanguage = "zh-CN",
                TotalSegments = 1,
                SelectedSegments = 1,
                CreatedAt = DateTimeOffset.UtcNow
            });
            writeStarted.SetResult();
            await database.SaveChangesAsync();
        });
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstToComplete = await Task.WhenAny(concurrentWrite, Task.Delay(250));
        interceptor.Release();

        var response = await listTask;
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        await concurrentWrite;

        Assert.NotSame(concurrentWrite, firstToComplete);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("created", item.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("latestTranslationRun").ValueKind);
    }

    private static HttpClient CreateClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
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

    private sealed class PauseBeforeRunQueryInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _runQueryReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task RunQueryReached => _runQueryReached.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                command.CommandText.Contains("FROM \"TranslationRuns\"", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _armed, 0);
                _runQueryReached.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}

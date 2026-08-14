using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class TranslationRunServiceTests
{
    private static readonly Guid TaskId = Guid.Parse("4d898d78-1f24-47e9-8e30-adcee716c13d");
    private static readonly Guid RunId = Guid.Parse("56ccae85-4fea-43d6-a736-644ac9ea2422");

    private static TranslationRunService Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out StubHttpMessageHandler handler,
        string? baseUrl = null)
    {
        handler = new StubHttpMessageHandler(responder);
        var js = new FakeJSRuntime();
        var nav = new FakeNavigationManager("http://localhost/");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var auth = new AuthStateProvider(new TokenStore(js));
        var client = new ApiClient(http, auth, nav, NullLogger<ApiClient>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Api:BaseUrl"] = baseUrl })
            .Build();
        return new TranslationRunService(client, config);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task CreateRunAsync_发送camelCase请求体且源语言空白转为null()
    {
        string? capturedBody = null;
        var service = Create(
            request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(HttpStatusCode.Accepted, CreateRun());
            },
            out var handler);

        await service.CreateRunAsync(TaskId, 1, null, "zh-CN");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs", request.RequestUri!.PathAndQuery);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("\"extractionRevision\":1", capturedBody);
        Assert.Contains("\"sourceLanguage\":null", capturedBody);
        Assert.Contains("\"targetLanguage\":\"zh-CN\"", capturedBody);
    }

    [Fact]
    public async Task CreateRunAsync_按契约反序列化202创建响应()
    {
        var json = """
            {
              "runId": "56ccae85-4fea-43d6-a736-644ac9ea2422",
              "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
              "extractionRevision": 1,
              "status": "queued",
              "sourceLanguage": "en",
              "targetLanguage": "zh-CN",
              "selection": {
                "totalSegments": 42,
                "selectedSegments": 40,
                "skippedExistingSegments": 2
              },
              "progress": {
                "processedSegments": 0,
                "succeededSegments": 0,
                "failedSegments": 0,
                "percent": 0.0
              },
              "failure": null,
              "createdAt": "2026-08-14T08:30:00Z",
              "startedAt": null,
              "finishedAt": null
            }
            """;
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            out _);

        var run = await service.CreateRunAsync(TaskId, 1, "en", "zh-CN");

        Assert.Equal(RunId, run.RunId);
        Assert.Equal(TaskId, run.TaskId);
        Assert.Equal(1, run.ExtractionRevision);
        Assert.Equal("queued", run.Status);
        Assert.Equal("en", run.SourceLanguage);
        Assert.Equal("zh-CN", run.TargetLanguage);
        Assert.Equal(42, run.Selection!.TotalSegments);
        Assert.Equal(40, run.Selection.SelectedSegments);
        Assert.Equal(2, run.Selection.SkippedExistingSegments);
        Assert.Equal(0, run.Progress!.ProcessedSegments);
        Assert.Equal(0.0, run.Progress.Percent);
        Assert.Null(run.Failure);
        Assert.Null(run.StartedAt);
        Assert.Null(run.FinishedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-08-14T08:30:00Z"), run.CreatedAt);
    }

    [Fact]
    public async Task GetRunAsync_解析RetryAfter秒数()
    {
        var service = Create(
            _ =>
            {
                var response = JsonResponse(HttpStatusCode.OK, CreateRun(status: "processing", processed: 24, succeeded: 23, failed: 1, percent: 60.0));
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return response;
            },
            out var handler);

        var poll = await service.GetRunAsync(TaskId, RunId);

        Assert.Equal(2, poll.RetryAfterSeconds);
        Assert.Equal("processing", poll.Run.Status);
        Assert.Equal(24, poll.Run.Progress!.ProcessedSegments);
        Assert.Equal(60.0, poll.Run.Progress.Percent);
        Assert.Equal(
            "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs/56ccae85-4fea-43d6-a736-644ac9ea2422",
            handler.Requests[0].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetRunAsync_无RetryAfter头_返回null()
    {
        var service = Create(_ => JsonResponse(HttpStatusCode.OK, CreateRun()), out _);

        var poll = await service.GetRunAsync(TaskId, RunId);

        Assert.Null(poll.RetryAfterSeconds);
    }

    [Fact]
    public async Task ListRunsAsync_分页参数与契约形状()
    {
        var json = """
            {
              "extractionRevision": 1,
              "items": [
                {
                  "runId": "56ccae85-4fea-43d6-a736-644ac9ea2422",
                  "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
                  "extractionRevision": 1,
                  "status": "partial_failed",
                  "sourceLanguage": "en",
                  "targetLanguage": "zh-CN",
                  "selection": { "totalSegments": 42, "selectedSegments": 40, "skippedExistingSegments": 2 },
                  "progress": { "processedSegments": 40, "succeededSegments": 39, "failedSegments": 1, "percent": 100.0 },
                  "failure": { "code": "segment_translation_failed", "retryable": true, "failedSegments": 1 },
                  "createdAt": "2026-08-14T08:30:00Z",
                  "startedAt": "2026-08-14T08:30:01Z",
                  "finishedAt": "2026-08-14T08:31:20Z"
                }
              ],
              "nextCursor": null
            }
            """;
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            out var handler);

        var page = await service.ListRunsAsync(TaskId, 1, null);
        await service.ListRunsAsync(TaskId, 100, "a/b c+=");

        Assert.Equal(
            "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs?limit=1",
            handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(
            "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs?limit=100&cursor=a%2Fb%20c%2B%3D",
            handler.Requests[1].RequestUri!.PathAndQuery);
        var run = Assert.Single(page.Items);
        Assert.Equal(RunId, run.RunId);
        Assert.Equal("partial_failed", run.Status);
        Assert.Equal("segment_translation_failed", run.Failure!.Code);
        Assert.True(run.Failure.Retryable);
        Assert.Equal(1, run.Failure.FailedSegments);
        Assert.Equal(39, run.Progress!.SucceededSegments);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetFailuresAsync_按契约反序列化分段级失败()
    {
        var json = """
            {
              "runId": "56ccae85-4fea-43d6-a736-644ac9ea2422",
              "items": [
                {
                  "segmentId": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
                  "segmentOrder": 17,
                  "code": "llm_provider_unavailable",
                  "retryable": true,
                  "attempts": 3
                }
              ],
              "nextCursor": null
            }
            """;
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            out var handler);

        var page = await service.GetFailuresAsync(TaskId, RunId, 100, null);

        Assert.Equal(
            "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs/56ccae85-4fea-43d6-a736-644ac9ea2422/failures?limit=100",
            handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(RunId, page.RunId);
        var failure = Assert.Single(page.Items);
        Assert.Equal("7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b", failure.SegmentId.ToString());
        Assert.Equal(17, failure.SegmentOrder);
        Assert.Equal("llm_provider_unavailable", failure.Code);
        Assert.True(failure.Retryable);
        Assert.Equal(3, failure.Attempts);
    }

    [Fact]
    public async Task CreateRunAsync_409ProblemDetails_抛出携带code的异常()
    {
        var problem = """
            {
              "type": "https://mytranslator.local/problems/no-segments-to-translate",
              "title": "No segments to translate",
              "status": 409,
              "code": "no_segments_to_translate",
              "instance": "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/translation-runs"
            }
            """;
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(problem, Encoding.UTF8, "application/problem+json"),
            },
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(
            () => service.CreateRunAsync(TaskId, 1, null, "zh-CN"));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("no_segments_to_translate", ex.Code);
        Assert.EndsWith("/translation-runs", ex.Instance);
    }

    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(0.4, 1)]
    [InlineData(2.6, 3)]
    public void ParseRetryAfter_Delta秒数向上取整且至少1秒(double seconds, int expected)
    {
        Assert.Equal(expected, TranslationRunService.ParseRetryAfter(
            new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds))));
    }

    [Fact]
    public void ParseRetryAfter_Date时间点换算剩余秒数且至少1秒()
    {
        // 3.5 秒后的时间点：即使换算受调用时序影响 ±0.2 秒，向上取整结果仍稳定为 4
        var date = DateTimeOffset.UtcNow.AddSeconds(3.5);
        Assert.Equal(4, TranslationRunService.ParseRetryAfter(new RetryConditionHeaderValue(date)));

        var past = DateTimeOffset.UtcNow.AddSeconds(-5);
        Assert.Equal(1, TranslationRunService.ParseRetryAfter(new RetryConditionHeaderValue(past)));

        Assert.Null(TranslationRunService.ParseRetryAfter(null));
    }

    private static object CreateRun(
        string? status = "queued",
        int processed = 0,
        int succeeded = 0,
        int failed = 0,
        double percent = 0.0)
    {
        return new
        {
            runId = RunId,
            taskId = TaskId,
            extractionRevision = 1,
            status,
            sourceLanguage = (string?)"en",
            targetLanguage = "zh-CN",
            selection = new { totalSegments = 42, selectedSegments = 40, skippedExistingSegments = 2 },
            progress = new { processedSegments = processed, succeededSegments = succeeded, failedSegments = failed, percent },
            failure = (object?)null,
            createdAt = "2026-08-14T08:30:00Z",
            startedAt = (string?)null,
            finishedAt = (string?)null,
        };
    }
}

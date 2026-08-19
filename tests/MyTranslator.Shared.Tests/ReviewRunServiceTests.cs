using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class ReviewRunServiceTests
{
    private static readonly Guid TaskId = Guid.Parse("4d898d78-1f24-47e9-8e30-adcee716c13d");
    private static readonly Guid RunId = Guid.Parse("8060fd5f-4d8e-4149-b973-c828b77ff9d8");

    private static ReviewRunService Create(
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
        return new ReviewRunService(client, config);
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

        await service.CreateRunAsync(TaskId, 1, "  ", "zh-CN");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/review-runs", request.RequestUri!.PathAndQuery);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("\"extractionRevision\":1", capturedBody);
        Assert.Contains("\"sourceLanguage\":null", capturedBody);
        Assert.Contains("\"targetLanguage\":\"zh-CN\"", capturedBody);
    }

    [Fact]
    public async Task CreateRunAsync_携带提供商模型三档选择()
    {
        string? capturedBody = null;
        var providerId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        var modelId = Guid.Parse("f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b");
        var service = Create(
            request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(HttpStatusCode.Accepted, CreateRun());
            },
            out _);

        await service.CreateRunAsync(TaskId, 1, "en", "zh-CN", providerId, modelId);

        Assert.Contains($"\"providerId\":\"{providerId}\"", capturedBody);
        Assert.Contains($"\"modelId\":\"{modelId}\"", capturedBody);
    }

    [Fact]
    public async Task CreateRunAsync_按契约反序列化202创建响应()
    {
        var json = """
            {
              "runId": "8060fd5f-4d8e-4149-b973-c828b77ff9d8",
              "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
              "extractionRevision": 1,
              "status": "queued",
              "sourceLanguage": "en",
              "targetLanguage": "zh-CN",
              "providerId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
              "modelId": "f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b",
              "selection": {
                "totalSegments": 42,
                "selectedSegments": 40,
                "skippedUntranslatedSegments": 2
              },
              "progress": {
                "processedSegments": 0,
                "succeededSegments": 0,
                "failedSegments": 0,
                "percent": 0.0
              },
              "failure": null,
              "createdAt": "2026-08-19T08:30:00Z",
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

        var poll = await service.CreateRunAsync(TaskId, 1, "en", "zh-CN");
        var run = poll.Run;

        Assert.Equal(RunId, run.RunId);
        Assert.Equal(TaskId, run.TaskId);
        Assert.Equal(1, run.ExtractionRevision);
        Assert.Equal("queued", run.Status);
        Assert.Equal("en", run.SourceLanguage);
        Assert.Equal("zh-CN", run.TargetLanguage);
        Assert.Equal(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), run.ProviderId);
        Assert.Equal(Guid.Parse("f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b"), run.ModelId);
        Assert.Equal(42, run.Selection!.TotalSegments);
        Assert.Equal(40, run.Selection.SelectedSegments);
        Assert.Equal(2, run.Selection.SkippedUntranslatedSegments);
        Assert.Equal(0, run.Progress!.ProcessedSegments);
        Assert.Null(run.Failure);
        Assert.Null(run.StartedAt);
        Assert.Null(run.FinishedAt);
        Assert.Null(poll.RetryAfterSeconds);
    }

    [Fact]
    public async Task CreateRunAsync_解析202RetryAfter头()
    {
        var service = Create(
            _ =>
            {
                var response = JsonResponse(HttpStatusCode.Accepted, CreateRun());
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return response;
            },
            out _);

        var poll = await service.CreateRunAsync(TaskId, 1, null, "zh-CN");

        Assert.Equal(1, poll.RetryAfterSeconds);
    }

    [Fact]
    public async Task GetRunAsync_请求正确路径并解析终态失败摘要()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, TerminalFailedRun()),
            out var handler);

        var poll = await service.GetRunAsync(TaskId, RunId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/api/tasks/{TaskId}/review-runs/{RunId}", request.RequestUri!.PathAndQuery);
        var run = poll.Run;
        Assert.Equal("partial_failed", run.Status);
        Assert.Equal("segment_review_failed", run.Failure!.Code);
        Assert.True(run.Failure.Retryable);
        Assert.Equal(1, run.Failure.FailedSegments);
        Assert.Equal(100.0, run.Progress!.Percent);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task ListRunsAsync_首页只带limit翻页附加转义游标()
    {
        string? secondQuery = null;
        var responderCall = 0;
        var service = Create(
            request =>
            {
                responderCall++;
                if (responderCall == 2)
                {
                    secondQuery = request.RequestUri!.PathAndQuery;
                }

                var body = responderCall == 1
                    ? "{\"extractionRevision\":1,\"items\":[],\"nextCursor\":\"opaque==/cursor\"}"
                    : "{\"extractionRevision\":1,\"items\":[],\"nextCursor\":null}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            },
            out var handler);

        var page = await service.ListRunsAsync(TaskId, 20, null);
        Assert.Equal("opaque==/cursor", page.NextCursor);

        var nextPage = await service.ListRunsAsync(TaskId, 20, page.NextCursor);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("limit=20", secondQuery);
        Assert.Contains("cursor=opaque%3D%3D%2Fcursor", secondQuery);
        Assert.Null(nextPage.NextCursor);
        Assert.Empty(nextPage.Items);
    }

    [Fact]
    public async Task GetFailuresAsync_按契约反序列化分段级失败()
    {
        var json = """
            {
              "runId": "8060fd5f-4d8e-4149-b973-c828b77ff9d8",
              "items": [
                {
                  "segmentId": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
                  "segmentOrder": 17,
                  "code": "llm_response_invalid",
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
            out _);

        var page = await service.GetFailuresAsync(TaskId, RunId, 100, null);

        var item = Assert.Single(page.Items);
        Assert.Equal(Guid.Parse("7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b"), item.SegmentId);
        Assert.Equal(17, item.SegmentOrder);
        Assert.Equal("llm_response_invalid", item.Code);
        Assert.True(item.Retryable);
        Assert.Equal(3, item.Attempts);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetFailuresAsync_非成功响应抛ApiError带错误码()
    {
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"type\":\"x\",\"title\":\"Not Found\",\"status\":404,\"code\":\"review_run_not_found\"}",
                    Encoding.UTF8,
                    "application/problem+json"),
            },
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(
            () => service.GetFailuresAsync(TaskId, RunId, 100, null));
        Assert.Equal("review_run_not_found", ex.Code);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void Segment_反序列化reviewComments_字段缺失时兜底空数组()
    {
        // §9：调用方必须容忍尚未升级的兼容服务省略该字段并按空数组处理
        var withoutField = """{"id":"7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b","order":17,"version":4,"markupTable":[]}""";
        var segment = JsonSerializer.Deserialize<Segment>(withoutField, ImportJson.Options);
        Assert.NotNull(segment);
        Assert.Empty(segment!.ReviewComments);

        var withField = """
            {
              "id": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
              "order": 17,
              "version": 4,
              "markupTable": [],
              "reviewComments": [
                { "severity": "high", "issue": "术语译法与规定译法不一致", "suggestion": "将“翻译缓存”改为“翻译记忆库”" },
                { "severity": "low", "issue": "风格建议", "suggestion": null }
              ]
            }
            """;
        var parsed = JsonSerializer.Deserialize<Segment>(withField, ImportJson.Options)!;
        Assert.Equal(2, parsed.ReviewComments.Count);
        Assert.Equal("high", parsed.ReviewComments[0].Severity);
        Assert.Equal("术语译法与规定译法不一致", parsed.ReviewComments[0].Issue);
        Assert.Equal("将“翻译缓存”改为“翻译记忆库”", parsed.ReviewComments[0].Suggestion);
        Assert.Equal("low", parsed.ReviewComments[1].Severity);
        Assert.Null(parsed.ReviewComments[1].Suggestion);
    }

    private static object CreateRun() => new
    {
        runId = RunId,
        taskId = TaskId,
        extractionRevision = 1,
        status = "queued",
        sourceLanguage = "en",
        targetLanguage = "zh-CN",
        providerId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
        modelId = Guid.Parse("f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b"),
        selection = new { totalSegments = 42, selectedSegments = 40, skippedUntranslatedSegments = 2 },
        progress = new { processedSegments = 0, succeededSegments = 0, failedSegments = 0, percent = 0.0 },
        failure = (object?)null,
        createdAt = "2026-08-19T08:30:00Z",
        startedAt = (object?)null,
        finishedAt = (object?)null,
    };

    private static object TerminalFailedRun() => new
    {
        runId = RunId,
        taskId = TaskId,
        extractionRevision = 1,
        status = "partial_failed",
        sourceLanguage = "en",
        targetLanguage = "zh-CN",
        providerId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
        modelId = Guid.Parse("f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b"),
        selection = new { totalSegments = 42, selectedSegments = 40, skippedUntranslatedSegments = 2 },
        progress = new { processedSegments = 40, succeededSegments = 39, failedSegments = 1, percent = 100.0 },
        failure = new { code = "segment_review_failed", retryable = true, failedSegments = 1 },
        createdAt = "2026-08-19T08:30:00Z",
        startedAt = "2026-08-19T08:30:01Z",
        finishedAt = "2026-08-19T08:31:20Z",
    };
}

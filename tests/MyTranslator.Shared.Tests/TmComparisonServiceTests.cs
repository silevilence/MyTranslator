using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 历史翻译对比客户端回归（docs/back/TM 接口约定.md §6.1：请求形状、响应解析、冲突错误传播）。
/// </summary>
public class TmComparisonServiceTests
{
    private static readonly Guid TaskId = Guid.Parse("4d898d78-1f24-47e9-8e30-adcee716c13d");
    private static readonly Guid SegmentId = Guid.Parse("7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b");
    private static readonly Guid EntryId = Guid.Parse("b9fa8ec4-b721-48b9-90e4-09ab14aa9403");

    private static TmComparisonService Create(
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
        return new TmComparisonService(client, config);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private static object CreateComparisonBody(bool withWarning = true)
    {
        return new
        {
            taskId = TaskId,
            segmentId = SegmentId,
            extractionRevision = 1,
            segmentVersion = 4,
            sourceLanguage = "en",
            targetLanguage = "zh-CN",
            thresholds = new { minimumSourceMatchScore = 0.7, warningSourceMatchScore = 0.85, warningTargetDifference = 0.3 },
            items = new[]
            {
                new
                {
                    entryId = EntryId,
                    sourceText = "Use translation memory.",
                    targetText = "使用翻译记忆库。",
                    markupTable = Array.Empty<object>(),
                    sourceMatchScore = 1.0,
                    targetSimilarity = 0.625,
                    targetDifference = 0.375,
                    createdAt = "2026-08-31T08:30:00Z",
                },
            },
            differenceWarning = withWarning
                ? (object)new { hasWarning = true, referenceEntryId = (Guid?)EntryId, sourceMatchScore = (double?)1.0, targetDifference = (double?)0.375, reason = "target_difference_exceeded" }
                : new { hasWarning = false, referenceEntryId = (Guid?)null, sourceMatchScore = (double?)null, targetDifference = (double?)null, reason = (string?)null },
            comparedAt = "2026-08-31T08:35:00Z",
        };
    }

    [Fact]
    public async Task 请求路径与查询参数_默认limit5()
    {
        var service = Create(_ => JsonResponse(HttpStatusCode.OK, CreateComparisonBody()), out var handler);

        await service.GetSegmentComparisonAsync(TaskId, SegmentId, 1, 4, "en", "zh-CN");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/tasks/{TaskId}/segments/{SegmentId}/tm-comparison", request.RequestUri!.AbsolutePath);
        var query = request.RequestUri!.Query;
        Assert.Contains("extractionRevision=1", query);
        Assert.Contains("segmentVersion=4", query);
        Assert.Contains("sourceLanguage=en", query);
        Assert.Contains("targetLanguage=zh-CN", query);
        Assert.Contains("limit=5", query);
    }

    [Fact]
    public async Task 自定义limit_随查询参数提交()
    {
        var service = Create(_ => JsonResponse(HttpStatusCode.OK, CreateComparisonBody()), out var handler);

        await service.GetSegmentComparisonAsync(TaskId, SegmentId, 1, 4, "en", "zh-CN", limit: 7);

        Assert.Contains("limit=7", Assert.Single(handler.Requests).RequestUri!.Query);
    }

    [Fact]
    public async Task 返回形状_解析阈值条目与告警()
    {
        var service = Create(_ => JsonResponse(HttpStatusCode.OK, CreateComparisonBody()), out var handler);

        var result = await service.GetSegmentComparisonAsync(TaskId, SegmentId, 1, 4, "en", "zh-CN");

        Assert.Equal(TaskId, result.TaskId);
        Assert.Equal(SegmentId, result.SegmentId);
        Assert.Equal(4, result.SegmentVersion);
        Assert.Equal(0.7, result.Thresholds!.MinimumSourceMatchScore);
        Assert.Equal(0.85, result.Thresholds.WarningSourceMatchScore);
        Assert.Equal(0.3, result.Thresholds.WarningTargetDifference);
        var item = Assert.Single(result.Items);
        Assert.Equal(EntryId, item.EntryId);
        Assert.Equal(1.0, item.SourceMatchScore);
        Assert.Equal(0.375, item.TargetDifference);
        Assert.True(result.DifferenceWarning!.HasWarning);
        Assert.Equal("target_difference_exceeded", result.DifferenceWarning.Reason);
    }

    [Fact]
    public async Task 无匹配_空条目且无告警()
    {
        var service = Create(
            _ => JsonResponse(
                HttpStatusCode.OK,
                new { taskId = TaskId, segmentId = SegmentId, extractionRevision = 1, segmentVersion = 4, sourceLanguage = "en", targetLanguage = "zh-CN", thresholds = new { minimumSourceMatchScore = 0.7, warningSourceMatchScore = 0.85, warningTargetDifference = 0.3 }, items = Array.Empty<object>(), differenceWarning = new { hasWarning = false, referenceEntryId = (Guid?)null, sourceMatchScore = (double?)null, targetDifference = (double?)null, reason = (string?)null }, comparedAt = "2026-08-31T08:35:00Z" }),
            out _);
        var result = await service.GetSegmentComparisonAsync(TaskId, SegmentId, 1, 4, "en", "zh-CN");

        Assert.Empty(result.Items);
        Assert.False(result.DifferenceWarning!.HasWarning);
        Assert.Null(result.DifferenceWarning.ReferenceEntryId);
    }

    [Fact]
    public async Task 分段版本冲突_抛出带errors的ApiErrorException()
    {
        var service = Create(
            _ => JsonResponse(
                HttpStatusCode.Conflict,
                new
                {
                    type = "urn:mytranslator:problem:segment-version-conflict",
                    title = "The segment has changed since it was read.",
                    status = 409,
                    code = "segment_version_conflict",
                    instance = $"/api/tasks/{TaskId}/segments/{SegmentId}/tm-comparison",
                    errors = new { requestedVersion = 3, currentVersion = 4 },
                }),
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(
            () => service.GetSegmentComparisonAsync(TaskId, SegmentId, 1, 3, "en", "zh-CN"));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("segment_version_conflict", ex.Code);
        Assert.Equal(3, ex.GetErrorInt("requestedVersion"));
        Assert.Equal(4, ex.GetErrorInt("currentVersion"));
    }

    [Fact]
    public async Task 提取修订变化_抛出对应错误码()
    {
        var service = Create(
            _ => JsonResponse(
                HttpStatusCode.Conflict,
                new { type = "urn:mytranslator:problem:extraction-revision-changed", title = "Revision changed.", status = 409, code = "extraction_revision_changed", instance = "/x" }),
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(
            () => service.GetSegmentComparisonAsync(TaskId, SegmentId, 1, 4, "en", "zh-CN"));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("extraction_revision_changed", ex.Code);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class ImportExportServiceTests
{
    private static readonly Guid TaskId = Guid.Parse("4d898d78-1f24-47e9-8e30-adcee716c13d");

    private static ImportExportService Create(
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
        return new ImportExportService(client, config);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task CreateFromFileAsync_上传multipart并携带参数()
    {
        Dictionary<string, (string? FileName, string? MediaType, string Body)>? captured = null;
        var service = Create(
            request =>
            {
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                captured = form.ToDictionary(
                    part => part.Headers.ContentDisposition!.Name!.Trim('"'),
                    part => (
                        part.Headers.ContentDisposition.FileName?.Trim('"'),
                        part.Headers.ContentType?.MediaType,
                        part.ReadAsStringAsync().GetAwaiter().GetResult()));
                return JsonResponse(HttpStatusCode.Created, CreateSummary());
            },
            out var handler);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("第一行\n第二行"));

        var summary = await service.CreateFromFileAsync(content, "manual.txt", "text/plain", "txt", "line");

        Assert.Equal(TaskId, summary.TaskId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/tasks/imports/file", request.RequestUri!.PathAndQuery);
        Assert.Equal(("manual.txt", "text/plain", "第一行\n第二行"), captured!["file"]);
        Assert.Equal((null, "text/plain", "txt"), captured["fileType"]);
        Assert.Equal((null, "text/plain", "line"), captured["segmentationMode"]);
    }

    [Fact]
    public async Task CreateFromFileAsync_可选参数缺省_不携带多余字段()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.Created, CreateSummary()),
            out var handler);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("abc"));

        await service.CreateFromFileAsync(content, "a.txt", null, null, null);

        var form = Assert.IsType<MultipartFormDataContent>(Assert.Single(handler.Requests).Content);
        Assert.DoesNotContain(form, p => p.Headers.ContentDisposition!.Name?.Trim('"') == "fileType");
        Assert.DoesNotContain(form, p => p.Headers.ContentDisposition!.Name?.Trim('"') == "segmentationMode");
    }

    [Fact]
    public async Task CreateFromUrlAsync_发送JSON且固定html类型()
    {
        string? capturedBody = null;
        var service = Create(
            request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(HttpStatusCode.Created, CreateSummary());
            },
            out var handler);

        await service.CreateFromUrlAsync("https://example.com/guide.html");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/tasks/imports/url", request.RequestUri!.PathAndQuery);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("\"url\":\"https://example.com/guide.html\"", capturedBody);
        Assert.Contains("\"fileType\":\"html\"", capturedBody);
    }

    [Fact]
    public async Task GetTaskAsync_配置了ApiBaseUrl_请求指向后端地址()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, CreateSummary()),
            out var handler,
            baseUrl: "http://backend:5199");

        await service.GetTaskAsync(TaskId);

        Assert.Equal(
            "http://backend:5199/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetSegmentsAsync_首页不带游标_翻页带转义游标()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, new { extractionRevision = 1, totalCount = 0, items = Array.Empty<object>(), nextCursor = (string?)null }),
            out var handler);

        await service.GetSegmentsAsync(TaskId, null);
        await service.GetSegmentsAsync(TaskId, "a/b c+=");

        Assert.Equal("/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/segments?limit=100", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(
            "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/segments?limit=100&cursor=a%2Fb%20c%2B%3D",
            handler.Requests[1].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetSegmentsAsync_按契约反序列化分段与标记表()
    {
        var json = """
            {
              "extractionRevision": 1,
              "totalCount": 42,
              "items": [
                {
                  "id": "7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b",
                  "order": 1,
                  "sourceText": "<x7>Read<x12/>this</x7>",
                  "targetText": null,
                  "confirmationStatus": "pending",
                  "version": 1,
                  "markupTable": [
                    { "id": 7, "kind": "paired", "openingText": "<strong>", "closingText": "</strong>", "originalText": null, "meaning": "加粗强调" },
                    { "id": 12, "kind": "standalone", "openingText": null, "closingText": null, "originalText": "&nbsp;", "meaning": "不间断空格" }
                  ],
                  "chapter": null
                }
              ],
              "nextCursor": "eyJyZXZpc2lvbiI6MS4uLn0"
            }
            """;
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            out _);

        var page = await service.GetSegmentsAsync(TaskId, null);

        Assert.Equal(1, page.ExtractionRevision);
        Assert.Equal(42, page.TotalCount);
        Assert.Equal("eyJyZXZpc2lvbiI6MS4uLn0", page.NextCursor);
        var segment = Assert.Single(page.Items);
        Assert.Equal("7d59abcf-eaf4-42d3-b1fe-f78bf08ca72b", segment.Id.ToString());
        Assert.Equal(1, segment.Order);
        Assert.Equal("<x7>Read<x12/>this</x7>", segment.SourceText);
        Assert.Null(segment.TargetText);
        Assert.Equal("pending", segment.ConfirmationStatus);
        Assert.Equal(2, segment.MarkupTable.Count);
        Assert.Equal("paired", segment.MarkupTable[0].Kind);
        Assert.Equal("<strong>", segment.MarkupTable[0].OpeningText);
        Assert.Equal("加粗强调", segment.MarkupTable[0].Meaning);
        Assert.Equal("standalone", segment.MarkupTable[1].Kind);
        Assert.Equal("&nbsp;", segment.MarkupTable[1].OriginalText);
        Assert.Equal("不间断空格", segment.MarkupTable[1].Meaning);
        Assert.Null(segment.Chapter);
    }

    [Fact]
    public async Task GetTasksAsync_首页不带筛选_筛选与翻页带参数()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, new { items = Array.Empty<object>(), nextCursor = (string?)null }),
            out var handler);

        await service.GetTasksAsync(null, null);
        await service.GetTasksAsync("processing", null);
        await service.GetTasksAsync("processing", "a/b c+=");

        Assert.Equal("/api/tasks?limit=100", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal("/api/tasks?limit=100&status=processing", handler.Requests[1].RequestUri!.PathAndQuery);
        Assert.Equal(
            "/api/tasks?limit=100&status=processing&cursor=a%2Fb%20c%2B%3D",
            handler.Requests[2].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetTasksAsync_按契约反序列化列表项与最新运行摘要()
    {
        var json = """
            {
              "items": [
                {
                  "taskId": "4d898d78-1f24-47e9-8e30-adcee716c13d",
                  "status": "processing",
                  "source": {
                    "kind": "file",
                    "fileName": "manual.txt",
                    "requestedUrl": null,
                    "finalUrl": null,
                    "mediaType": "text/plain",
                    "byteLength": 18240
                  },
                  "fileType": "txt",
                  "extractionRevision": 1,
                  "progress": {
                    "completedSegments": 24,
                    "totalSegments": 42
                  },
                  "latestTranslationRun": {
                    "runId": "56ccae85-4fea-43d6-a736-644ac9ea2422",
                    "status": "processing",
                    "progress": {
                      "processedSegments": 24,
                      "succeededSegments": 23,
                      "failedSegments": 1,
                      "percent": 60.0
                    },
                    "failure": null,
                    "createdAt": "2026-08-14T08:30:00Z",
                    "finishedAt": null
                  },
                  "createdAt": "2026-08-13T08:30:00Z"
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

        var page = await service.GetTasksAsync(null, null);

        Assert.Equal("/api/tasks?limit=100", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Null(page.NextCursor);
        var item = Assert.Single(page.Items);
        Assert.Equal(TaskId, item.TaskId);
        Assert.Equal("processing", item.Status);
        Assert.Equal("file", item.Source!.Kind);
        Assert.Equal("manual.txt", item.Source.FileName);
        Assert.Equal(18240, item.Source.ByteLength);
        Assert.Equal("txt", item.FileType);
        Assert.Equal(1, item.ExtractionRevision);
        Assert.Equal(24, item.Progress!.CompletedSegments);
        Assert.Equal(42, item.Progress.TotalSegments);
        Assert.Equal(57.1, item.Progress.Percent);
        var run = item.LatestTranslationRun!;
        Assert.Equal(Guid.Parse("56ccae85-4fea-43d6-a736-644ac9ea2422"), run.RunId);
        Assert.Equal("processing", run.Status);
        Assert.Equal(24, run.Progress!.ProcessedSegments);
        Assert.Equal(23, run.Progress.SucceededSegments);
        Assert.Equal(1, run.Progress.FailedSegments);
        Assert.Equal(60.0, run.Progress.Percent);
        Assert.Null(run.Failure);
        Assert.Null(run.FinishedAt);
    }

    [Fact]
    public async Task GetTaskAsync_按契约反序列化摘要()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, CreateSummary()),
            out _);

        var summary = await service.GetTaskAsync(TaskId);

        Assert.Equal(TaskId, summary.TaskId);
        Assert.Equal("created", summary.Status);
        Assert.Equal("file", summary.Source!.Kind);
        Assert.Equal("manual.txt", summary.Source.FileName);
        Assert.Equal("txt", summary.FileType);
        Assert.Equal("utf-8", summary.OriginalEncoding);
        Assert.Equal(1, summary.ExtractionRevision);
        Assert.Equal("paragraph", summary.Segmentation!.Recommended);
        Assert.Equal("blank_line_blocks_present", summary.Segmentation.Reason);
        Assert.Equal(42, summary.Counts!.Segments);
        Assert.Equal(7, summary.Counts.ProtectedBlocks);
        Assert.False(summary.Capabilities!.CanReextract);
        Assert.False(summary.Capabilities.CanExport);
    }

    [Fact]
    public async Task ExportAsync_解析ContentDisposition文件名并返回字节()
    {
        var bytes = Encoding.UTF8.GetBytes("<html><body>译文</body></html>");
        var service = Create(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = "guide.translated.html",
                };
                return response;
            },
            out var handler);

        var result = await service.ExportAsync(TaskId);

        Assert.Equal("/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/export", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal("guide.translated.html", result.FileName);
        Assert.Equal("text/html", result.MediaType);
        Assert.Equal(bytes, result.Content);
    }

    [Fact]
    public async Task ApplyReextractionAsync_409ProblemDetails_解析code与errors扩展()
    {
        var problem = """
            {
              "type": "https://mytranslator.local/problems/translation-loss-confirmation-required",
              "title": "Translation loss confirmation required",
              "status": 409,
              "code": "translation_loss_confirmation_required",
              "instance": "/api/tasks/4d898d78-1f24-47e9-8e30-adcee716c13d/extraction-previews/9a882f4c-b06a-4a86-9b8a-39b3dcd3a066/apply",
              "errors": { "translatedSegments": 18, "confirmedSegments": 6 }
            }
            """;
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(problem, Encoding.UTF8, "application/problem+json"),
            },
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(
            () => service.ApplyReextractionAsync(
                TaskId, Guid.Parse("9a882f4c-b06a-4a86-9b8a-39b3dcd3a066"), confirmTranslationLoss: false));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("translation_loss_confirmation_required", ex.Code);
        Assert.EndsWith("/apply", ex.Instance);
        Assert.Equal(18, ex.GetErrorInt("translatedSegments"));
        Assert.Equal(6, ex.GetErrorInt("confirmedSegments"));
    }

    [Fact]
    public async Task 非ProblemDetails错误体_仍抛出携带状态码的异常()
    {
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("plain text error"),
            },
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(() => service.GetTaskAsync(TaskId));

        Assert.Equal(500, ex.StatusCode);
        Assert.Null(ex.Code);
    }

    [Fact]
    public void ParseContentDispositionFileName_引号与filenameStar处理()
    {
        var quoted = new ContentDispositionHeaderValue("attachment") { FileName = "\"a b.txt\"" };
        Assert.Equal("a b.txt", ImportExportService.ParseContentDispositionFileName(quoted));

        var star = new ContentDispositionHeaderValue("attachment") { FileNameStar = "中文.txt" };
        Assert.Equal("中文.txt", ImportExportService.ParseContentDispositionFileName(star));

        Assert.Null(ImportExportService.ParseContentDispositionFileName(null));
    }

    private static object CreateSummary()
    {
        return new
        {
            taskId = TaskId,
            status = "created",
            source = new
            {
                kind = "file",
                fileName = "manual.txt",
                requestedUrl = (string?)null,
                finalUrl = (string?)null,
                mediaType = "text/plain",
                byteLength = 18240,
            },
            fileType = "txt",
            originalEncoding = "utf-8",
            extractionRevision = 1,
            segmentation = new
            {
                requested = (string?)null,
                recommended = "paragraph",
                effective = "paragraph",
                reason = "blank_line_blocks_present",
            },
            counts = new { segments = 42, protectedBlocks = 7, chapters = 0 },
            capabilities = new { canReextract = false, canExport = false },
            createdAt = "2026-08-13T08:30:00Z",
        };
    }
}

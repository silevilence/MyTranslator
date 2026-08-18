using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class TermServiceTests
{
    private static readonly Guid TermId = Guid.Parse("01f42ee7-2f9b-45e8-b753-a070253a230d");

    private static TermService Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out StubHttpMessageHandler handler)
    {
        handler = new StubHttpMessageHandler(responder);
        var js = new FakeJSRuntime();
        var nav = new FakeNavigationManager("http://localhost/");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var auth = new AuthStateProvider(new TokenStore(js));
        var client = new ApiClient(http, auth, nav, NullLogger<ApiClient>.Instance);
        var config = new ConfigurationBuilder().Build();
        return new TermService(client, config);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, object? errors = null)
    {
        return JsonResponse(status, new { type = $"https://mytranslator.local/problems/{code}", title = code, status = (int)status, code, errors });
    }

    private static object TermBody(
        string source = "translation memory",
        string target = "翻译记忆库",
        string? notes = null,
        int version = 1) => new
    {
        id = TermId,
        sourceTerm = source,
        targetTerm = target,
        sourceLanguage = "en",
        targetLanguage = "zh-CN",
        notes,
        caseSensitive = false,
        version,
        createdAt = "2026-08-18T08:30:00Z",
        updatedAt = "2026-08-18T08:30:00Z",
    };

    [Fact]
    public async Task GetTermsAsync_组装查询参数并解码响应()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, new
            {
                items = new object[]
                {
                    new
                    {
                        id = TermId,
                        sourceTerm = "translation",
                        targetTerm = "翻译",
                        sourceLanguage = "en",
                        targetLanguage = "zh-CN",
                        notes = (string?)null,
                        caseSensitive = false,
                        version = 3,
                        createdAt = "2026-08-18T08:30:00Z",
                        updatedAt = "2026-08-18T09:05:00Z",
                        matchScore = 0.8182,
                        matchedField = "source",
                    },
                },
                nextCursor = (string?)"b2FxdWU=",
            }),
            out var handler);

        var page = await service.GetTermsAsync("translatoin mem", "en", "zh-CN", null);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/terms?limit=100&query=translatoin%20mem&sourceLanguage=en&targetLanguage=zh-CN", request.RequestUri!.PathAndQuery);
        var item = Assert.Single(page.Items);
        Assert.Equal(0.8182, item.MatchScore);
        Assert.Equal("source", item.MatchedField);
        Assert.Equal(3, item.Version);
        Assert.Equal("b2FxdWU=", page.NextCursor);
    }

    [Fact]
    public async Task GetTermsAsync_空白筛选不携带参数并透传游标()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, new { items = Array.Empty<object>(), nextCursor = (string?)null }),
            out var handler);

        var page = await service.GetTermsAsync("  ", null, "", "cur=sor&x");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/terms?limit=100&cursor=cur%3Dsor%26x", request.RequestUri!.PathAndQuery);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetTermAsync_请求资源路径()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, TermBody()),
            out var handler);

        var term = await service.GetTermAsync(TermId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/api/terms/{TermId}", request.RequestUri!.PathAndQuery);
        Assert.Equal("translation memory", term.SourceTerm);
        Assert.Null(term.Notes);
    }

    [Fact]
    public async Task CreateTermAsync_提交创建请求体()
    {
        string? capturedBody = null;
        var service = Create(
            request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(HttpStatusCode.Created, TermBody(notes: "首次出现时可附英文缩写 TM。"));
            },
            out var handler);

        var created = await service.CreateTermAsync(new TermUpsert
        {
            SourceTerm = "translation memory",
            TargetTerm = "翻译记忆库",
            SourceLanguage = "en",
            TargetLanguage = "zh-CN",
            Notes = "首次出现时可附英文缩写 TM。",
            CaseSensitive = false,
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/terms", request.RequestUri!.PathAndQuery);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        using var document = JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Assert.Equal("translation memory", root.GetProperty("sourceTerm").GetString());
        Assert.Equal("翻译记忆库", root.GetProperty("targetTerm").GetString());
        Assert.Equal("en", root.GetProperty("sourceLanguage").GetString());
        Assert.Equal("zh-CN", root.GetProperty("targetLanguage").GetString());
        Assert.Equal("首次出现时可附英文缩写 TM。", root.GetProperty("notes").GetString());
        Assert.False(root.GetProperty("caseSensitive").GetBoolean());
        Assert.Equal(1, created.Version);
    }

    [Fact]
    public async Task UpdateTermAsync_完整替换并携带版本()
    {
        string? capturedBody = null;
        var service = Create(
            request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(HttpStatusCode.OK, TermBody(version: 2));
            },
            out var handler);

        var updated = await service.UpdateTermAsync(TermId, new TermUpsert
        {
            SourceTerm = "translation memory",
            TargetTerm = "翻译记忆库",
            SourceLanguage = "en",
            TargetLanguage = "zh-CN",
            CaseSensitive = true,
            Version = 1,
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/terms/{TermId}", request.RequestUri!.PathAndQuery);
        using var document = JsonDocument.Parse(capturedBody!);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.True(root.GetProperty("caseSensitive").GetBoolean());
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public async Task DeleteTermAsync_版本经查询参数提交()
    {
        var service = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent), out var handler);

        await service.DeleteTermAsync(TermId, 3);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/terms/{TermId}?version=3", request.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task 非成功响应_抛出携带错误码的异常()
    {
        var service = Create(
            _ => Problem(HttpStatusCode.Conflict, "term_version_conflict", new { requestedVersion = 2, currentVersion = 3 }),
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(() => service.UpdateTermAsync(TermId, new TermUpsert
        {
            SourceTerm = "a",
            TargetTerm = "b",
            SourceLanguage = "en",
            TargetLanguage = "zh-CN",
            Version = 2,
        }));

        Assert.Equal("term_version_conflict", ex.Code);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(3, ex.GetErrorInt("currentVersion"));
    }

    [Fact]
    public async Task 冲突响应_可读取字符串扩展字段()
    {
        var service = Create(
            _ => Problem(HttpStatusCode.Conflict, "term_conflict", new { conflictingTermId = TermId }),
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(() => service.CreateTermAsync(new TermUpsert
        {
            SourceTerm = "a",
            TargetTerm = "b",
            SourceLanguage = "en",
            TargetLanguage = "zh-CN",
        }));

        Assert.Equal("term_conflict", ex.Code);
        Assert.Equal(TermId.ToString(), ex.GetErrorString("conflictingTermId"));
    }
}

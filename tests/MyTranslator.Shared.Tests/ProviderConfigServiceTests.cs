using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class ProviderConfigServiceTests
{
    private static readonly Guid ProviderId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid ModelEntryId = Guid.Parse("f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b");

    private static ProviderConfigService Create(
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
        return new ProviderConfigService(client, config);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    [Fact]
    public async Task GetProvidersAsync_反序列化内嵌models的两级配置()
    {
        var json = """
            [
              {
                "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
                "name": "DeepSeek",
                "kind": "openai",
                "baseUrl": "https://api.deepseek.com",
                "apiKeyMasked": "sk-***cdef",
                "enabled": true,
                "isDefault": true,
                "batchSize": 20,
                "requestTimeout": "00:01:00",
                "maxAttempts": 3,
                "createdAt": "2026-08-14T08:30:00Z",
                "models": [
                  {
                    "id": "f1a2b3c4-d5e6-4f70-8a9b-0c1d2e3f4a5b",
                    "providerId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
                    "modelId": "deepseek-chat",
                    "displayName": "DeepSeek Chat",
                    "supportsThinking": false,
                    "supportsToolUse": false,
                    "supportsStreaming": true,
                    "isDefault": true,
                    "createdAt": "2026-08-14T08:31:00Z"
                  }
                ]
              }
            ]
            """;
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            out _);

        var providers = await service.GetProvidersAsync();

        var provider = Assert.Single(providers);
        Assert.Equal("DeepSeek", provider.Name);
        Assert.Equal("openai", provider.Kind);
        Assert.Equal("https://api.deepseek.com", provider.BaseUrl);
        Assert.Equal("sk-***cdef", provider.ApiKeyMasked);
        Assert.True(provider.Enabled);
        Assert.True(provider.IsDefault);
        Assert.Equal(20, provider.BatchSize);
        Assert.Equal("00:01:00", provider.RequestTimeout);
        Assert.Equal(3, provider.MaxAttempts);
        var model = Assert.Single(provider.Models);
        Assert.Equal(ModelEntryId, model.Id);
        Assert.Equal(ProviderId, model.ProviderId);
        Assert.Equal("deepseek-chat", model.ModelId);
        Assert.Equal("DeepSeek Chat", model.DisplayName);
        Assert.True(model.SupportsStreaming);
        Assert.False(model.SupportsThinking);
        Assert.False(model.SupportsToolUse);
        Assert.True(model.IsDefault);
    }

    [Fact]
    public async Task CreateProviderAsync_发送camelCase请求体()
    {
        string? capturedBody = null;
        var service = Create(
            request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(HttpStatusCode.Created, new { id = ProviderId.ToString() });
            },
            out var handler);

        await service.CreateProviderAsync(new AiProviderUpsert
        {
            Name = "DeepSeek",
            Kind = "openai",
            BaseUrl = "https://api.deepseek.com",
            ApiKey = "sk-0123456789abcdef0123456789abcdef",
            Enabled = true,
            IsDefault = true,
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/providers", request.RequestUri!.PathAndQuery);
        Assert.Contains("\"name\":\"DeepSeek\"", capturedBody);
        Assert.Contains("\"kind\":\"openai\"", capturedBody);
        Assert.Contains("\"apiKey\":\"sk-0123456789abcdef0123456789abcdef\"", capturedBody);
        Assert.DoesNotContain("apiKeyMasked", capturedBody);
    }

    [Fact]
    public async Task UpdateProviderAsync_apiKey为null时请求体携带null_保持原密钥()
    {
        string? capturedBody = null;
        var service = Create(
            request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(HttpStatusCode.OK, new { id = ProviderId.ToString() });
            },
            out var handler);

        await service.UpdateProviderAsync(ProviderId, new AiProviderUpsert
        {
            Name = "DeepSeek",
            Kind = "openai",
            ApiKey = null,
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/providers/{ProviderId}", request.RequestUri!.PathAndQuery);
        Assert.Contains("\"apiKey\":null", capturedBody);
    }

    [Fact]
    public async Task DeleteProviderAsync_请求正确路径且204不抛出()
    {
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            out var handler);

        await service.DeleteProviderAsync(ProviderId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/providers/{ProviderId}", request.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task CreateModelAsync_子资源路径与请求体()
    {
        string? capturedBody = null;
        var service = Create(
            request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(HttpStatusCode.Created, new { id = ModelEntryId.ToString() });
            },
            out var handler);

        await service.CreateModelAsync(ProviderId, new AiModelUpsert
        {
            ModelId = "deepseek-chat",
            DisplayName = "DeepSeek Chat",
            SupportsStreaming = true,
            IsDefault = true,
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/providers/{ProviderId}/models", request.RequestUri!.PathAndQuery);
        Assert.Contains("\"modelId\":\"deepseek-chat\"", capturedBody);
        Assert.Contains("\"supportsStreaming\":true", capturedBody);
    }

    [Fact]
    public async Task UpdateModelAsync_以模型条目ID为路径资源()
    {
        var service = Create(
            _ => JsonResponse(HttpStatusCode.OK, new { id = ModelEntryId.ToString() }),
            out var handler);

        await service.UpdateModelAsync(ProviderId, ModelEntryId, new AiModelUpsert
        {
            ModelId = "deepseek-reasoner",
            DisplayName = "DeepSeek Reasoner",
        });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/api/providers/{ProviderId}/models/{ModelEntryId}", request.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task DeleteModelAsync_请求正确路径()
    {
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            out var handler);

        await service.DeleteModelAsync(ProviderId, ModelEntryId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"/api/providers/{ProviderId}/models/{ModelEntryId}", request.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task 非成功响应抛出携带错误码的ApiErrorException()
    {
        var problem = """
            { "type": "about:blank", "title": "Conflict", "status": 409, "code": "model_id_conflict" }
            """;
        var service = Create(
            _ => new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(problem, Encoding.UTF8, "application/problem+json"),
            },
            out _);

        var ex = await Assert.ThrowsAsync<ApiErrorException>(
            () => service.CreateModelAsync(ProviderId, new AiModelUpsert { ModelId = "dup", DisplayName = "dup" }));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("model_id_conflict", ex.Code);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>
/// AI 提供商/模型配置管理接口客户端（docs/back/AI 配置管理接口约定.md）。
/// 覆盖提供商与模型的增删改查（GET /api/providers 内嵌 models，一次加载两级配置）。
/// 非成功响应统一抛出 <see cref="ApiErrorException"/>，
/// 由页面经 <see cref="ApiErrorMessageProvider"/> 映射文案。
/// </summary>
public class ProviderConfigService
{
    private readonly ApiClient _api;
    private readonly string _baseUrl;

    public ProviderConfigService(ApiClient api, IConfiguration config)
    {
        _api = api;
        _baseUrl = config["Api:BaseUrl"] ?? string.Empty;
    }

    /// <summary>拼接接口地址：配置了 <c>Api:BaseUrl</c> 时指向后端，否则使用页面同源相对路径。</summary>
    private string ApiUrl(string path) => _baseUrl + path;

    /// <summary>
    /// 列出全部提供商（§4.4），按创建时间升序；每个对象内嵌完整 models 数组，
    /// 供设置页与翻译面板选择器一次加载两级配置。
    /// </summary>
    public async Task<IReadOnlyList<AiProvider>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync(ApiUrl("/api/providers"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AiProvider>>(ImportJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("接口响应缺少提供商列表内容");
    }

    /// <summary>创建提供商（§4 POST）。成功返回 201 与掩码回显的提供商。</summary>
    public async Task<AiProvider> CreateProviderAsync(
        AiProviderUpsert request,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = CreateJsonRequest(HttpMethod.Post, ApiUrl("/api/providers"), request);
        return await SendForResultAsync<AiProvider>(httpRequest, cancellationToken);
    }

    /// <summary>
    /// 全字段替换更新提供商（§4 PUT）；<see cref="AiProviderUpsert.ApiKey"/> 为 null/空白时保持原密钥。
    /// </summary>
    public async Task<AiProvider> UpdateProviderAsync(
        Guid providerId,
        AiProviderUpsert request,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = CreateJsonRequest(HttpMethod.Put, $"{ApiUrl("/api/providers/")}{providerId}", request);
        return await SendForResultAsync<AiProvider>(httpRequest, cancellationToken);
    }

    /// <summary>删除提供商（§4 DELETE）；级联删除其下全部模型，成功返回 204。</summary>
    public async Task DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiUrl("/api/providers/")}{providerId}");
        await SendForNoContentAsync(request, cancellationToken);
    }

    /// <summary>在提供商下创建模型（§5 POST）。成功返回 201 与完整模型对象。</summary>
    public async Task<AiModel> CreateModelAsync(
        Guid providerId,
        AiModelUpsert request,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = CreateJsonRequest(
            HttpMethod.Post, $"{ApiUrl("/api/providers/")}{providerId}/models", request);
        return await SendForResultAsync<AiModel>(httpRequest, cancellationToken);
    }

    /// <summary>
    /// 全字段替换更新模型（§5 PUT）。路径中的模型 ID 为模型条目 ID（非厂商模型 ID）；
    /// 请求体 <see cref="AiModelUpsert.ModelId"/> 为厂商模型 ID，变更后仍须提供商内唯一。
    /// </summary>
    public async Task<AiModel> UpdateModelAsync(
        Guid providerId,
        Guid modelEntryId,
        AiModelUpsert request,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = CreateJsonRequest(
            HttpMethod.Put, $"{ApiUrl("/api/providers/")}{providerId}/models/{modelEntryId}", request);
        return await SendForResultAsync<AiModel>(httpRequest, cancellationToken);
    }

    /// <summary>删除模型（§5 DELETE）；成功返回 204。</summary>
    public async Task DeleteModelAsync(
        Guid providerId,
        Guid modelEntryId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{ApiUrl("/api/providers/")}{providerId}/models/{modelEntryId}");
        await SendForNoContentAsync(request, cancellationToken);
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, object body)
    {
        return new HttpRequestMessage(method, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, ImportJson.Options), Encoding.UTF8, "application/json"),
        };
    }

    private async Task<T> SendForResultAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _api.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(ImportJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"接口响应缺少 {typeof(T).Name} 内容");
    }

    private async Task SendForNoContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _api.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent && !response.IsSuccessStatusCode)
        {
            throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
        }
    }
}

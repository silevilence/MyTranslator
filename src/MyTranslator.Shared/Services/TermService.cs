using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 术语表管理接口客户端（docs/back/术语接口约定.md）。
/// 覆盖术语增删改查与游标分页模糊查询（GET / POST / GET {id} / PUT / DELETE?version=）。
/// 非成功响应统一抛出 <see cref="ApiErrorException"/>，
/// 由页面经 <see cref="ApiErrorMessageProvider"/> 映射文案。
/// </summary>
public class TermService
{
    private const string PageSize = "100";

    private readonly ApiClient _api;
    private readonly string _baseUrl;

    public TermService(ApiClient api, IConfiguration config)
    {
        _api = api;
        _baseUrl = config["Api:BaseUrl"] ?? string.Empty;
    }

    /// <summary>拼接接口地址：配置了 <c>Api:BaseUrl</c> 时指向后端，否则使用页面同源相对路径。</summary>
    private string ApiUrl(string path) => _baseUrl + path;

    /// <summary>
    /// 分页列出/模糊查询术语（§5）。
    /// 游标绑定产生它的查询条件：query、sourceLanguage、targetLanguage 任一变化后
    /// 必须丢弃旧游标并从第一页（<paramref name="cursor"/> = null）重新加载。
    /// </summary>
    /// <param name="query">同时模糊匹配源/目标术语；null 或纯空白表示不做模糊筛选。</param>
    /// <param name="sourceLanguage">精确筛选规范化后的源语言标签；null 表示不筛选。</param>
    /// <param name="targetLanguage">精确筛选规范化后的目标语言标签；null 表示不筛选。</param>
    /// <param name="cursor">上一页返回的不透明游标；首页传 null。</param>
    public async Task<TermListPage> GetTermsAsync(
        string? query,
        string? sourceLanguage,
        string? targetLanguage,
        string? cursor,
        CancellationToken cancellationToken = default, Guid? glossaryId = null)
    {
        var requestUrl = new StringBuilder($"{ApiUrl("/api/terms")}?limit={PageSize}");
        if (glossaryId is { } id) requestUrl.Append("&glossaryId=").Append(id);
        if (!string.IsNullOrWhiteSpace(query))
        {
            requestUrl.Append("&query=").Append(Uri.EscapeDataString(query.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            requestUrl.Append("&sourceLanguage=").Append(Uri.EscapeDataString(sourceLanguage.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            requestUrl.Append("&targetLanguage=").Append(Uri.EscapeDataString(targetLanguage.Trim()));
        }

        if (cursor is not null)
        {
            requestUrl.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        }

        using var response = await _api.GetAsync(requestUrl.ToString(), cancellationToken);
        return await ReadResultAsync<TermListPage>(response, cancellationToken);
    }

    /// <summary>读取单个术语（§6）；不含列表查询专用的 matchScore / matchedField。</summary>
    public async Task<Term> GetTermAsync(Guid termId, CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync($"{ApiUrl("/api/terms/")}{termId}", cancellationToken);
        return await ReadResultAsync<Term>(response, cancellationToken);
    }

    /// <summary>创建术语（§4 POST）；成功返回 201 与完整术语（version = 1）。</summary>
    public async Task<Term> CreateTermAsync(TermUpsert request, CancellationToken cancellationToken = default)
    {
        using var httpRequest = CreateJsonRequest(HttpMethod.Post, ApiUrl("/api/terms"), request);
        return await SendForResultAsync<Term>(httpRequest, cancellationToken);
    }

    /// <summary>
    /// 完整替换更新术语（§7 PUT）；请求体必须携带读取时的版本，
    /// 版本过期时服务端返回 409 term_version_conflict，由调用方重新读取后交用户决定是否重新提交。
    /// </summary>
    public async Task<Term> UpdateTermAsync(Guid termId, TermUpsert request, CancellationToken cancellationToken = default)
    {
        using var httpRequest = CreateJsonRequest(HttpMethod.Put, $"{ApiUrl("/api/terms/")}{termId}", request);
        return await SendForResultAsync<Term>(httpRequest, cancellationToken);
    }

    /// <summary>删除术语（§8 DELETE）；版本经查询参数提交，成功返回 204。</summary>
    public async Task DeleteTermAsync(Guid termId, int version, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{ApiUrl("/api/terms/")}{termId}?version={version}");
        using var response = await _api.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Glossary>> GetGlossariesAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<Glossary>();
        string? cursor = null;
        do
        {
            using var response = await _api.GetAsync(ApiUrl("/api/glossaries?limit=200") +
                (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), cancellationToken);
            var page = await ReadResultAsync<GlossaryPage>(response, cancellationToken);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return items;
    }

    public async Task<Glossary> SaveGlossaryAsync(string name, Glossary? glossary, CancellationToken cancellationToken = default)
    {
        using var request = CreateJsonRequest(glossary is null ? HttpMethod.Post : HttpMethod.Put,
            ApiUrl("/api/glossaries") + (glossary is null ? "" : $"/{glossary.Id}"), new { name, version = glossary?.Version });
        return await SendForResultAsync<Glossary>(request, cancellationToken);
    }

    public async Task DeleteGlossaryAsync(Glossary glossary, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ApiUrl($"/api/glossaries/{glossary.Id}?version={glossary.Version}"));
        using var response = await _api.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
    }

    public async Task<TaskReference> GetReferenceAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync(ApiUrl($"/api/tasks/{taskId}/reference"), cancellationToken);
        return await ReadResultAsync<TaskReference>(response, cancellationToken);
    }

    public async Task<TaskReference> SaveReferenceAsync(Guid taskId, TaskReference reference, CancellationToken cancellationToken = default)
    {
        using var request = CreateJsonRequest(HttpMethod.Put, ApiUrl($"/api/tasks/{taskId}/reference"), reference);
        return await SendForResultAsync<TaskReference>(request, cancellationToken);
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
        return await ReadResultAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(ImportJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"接口响应缺少 {typeof(T).Name} 内容");
    }
}

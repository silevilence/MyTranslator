using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 查询审核运行详情并附带的轮询间隔（§5：调用方应遵守 Retry-After；未返回时轮询间隔不得短于 1 秒）。
/// </summary>
/// <param name="Run">运行快照。</param>
/// <param name="RetryAfterSeconds">响应 Retry-After 表示的秒数；头缺失或不可解析时为 null。</param>
public sealed record ReviewRunPoll(ReviewRun Run, int? RetryAfterSeconds);

/// <summary>
/// AI 审核运行接口客户端（docs/back/AI 审核接口约定.md）。
/// 覆盖创建审核运行、查询运行、列出运行与读取分段级失败；
/// 审核意见本身随分段响应下发（§9），不单独查询。
/// 接口地址经配置 <c>Api:BaseUrl</c> 注入；非成功响应统一抛出
/// <see cref="ApiErrorException"/>，由页面经 <see cref="ApiErrorMessageProvider"/> 映射文案。
/// </summary>
public class ReviewRunService
{
    private readonly ApiClient _api;
    private readonly string _baseUrl;

    public ReviewRunService(ApiClient api, IConfiguration config)
    {
        _api = api;
        _baseUrl = config["Api:BaseUrl"] ?? string.Empty;
    }

    /// <summary>拼接接口地址：配置了 <c>Api:BaseUrl</c> 时指向后端，否则使用页面同源相对路径。</summary>
    private string ApiUrl(string path) => _baseUrl + path;

    /// <summary>
    /// 创建审核运行（§4）。<paramref name="sourceLanguage"/> 为 null 或空白表示由 LLM 自动识别（不注入术语）；
    /// <paramref name="providerId"/>/<paramref name="modelId"/> 按三档解析（§3.1）：
    /// 都为 null 走默认对，只传 providerId 用该提供商默认模型，都传为精确指定。
    /// 审核不改变任务公共状态，返回的运行状态为 <c>queued</c>，
    /// 并附创建响应 Retry-After 作为首轮询间隔参考。
    /// </summary>
    public async Task<ReviewRunPoll> CreateRunAsync(
        Guid taskId,
        int extractionRevision,
        string? sourceLanguage,
        string targetLanguage,
        Guid? providerId = null,
        Guid? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(
            new
            {
                extractionRevision,
                sourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage) ? null : sourceLanguage,
                targetLanguage,
                providerId,
                modelId,
            },
            ImportJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl("/api/tasks/")}{taskId}/review-runs")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await _api.SendAsync(request, cancellationToken);
        var run = await ReadResultAsync<ReviewRun>(response, cancellationToken);
        return new ReviewRunPoll(run, TranslationRunService.ParseRetryAfter(response.Headers.RetryAfter));
    }

    /// <summary>查询审核运行（§5）。活动运行响应携带 Retry-After，调用方据此安排轮询。</summary>
    public async Task<ReviewRunPoll> GetRunAsync(
        Guid taskId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/review-runs/{runId}",
            cancellationToken);
        var run = await ReadResultAsync<ReviewRun>(response, cancellationToken);
        return new ReviewRunPoll(run, TranslationRunService.ParseRetryAfter(response.Headers.RetryAfter));
    }

    /// <summary>列出当前提取修订的审核运行（§6），按 createdAt 降序；limit 范围 1..200。</summary>
    public async Task<ReviewRunPage> ListRunsAsync(
        Guid taskId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/review-runs{PageQuery(limit, cursor)}",
            cancellationToken);
        return await ReadResultAsync<ReviewRunPage>(response, cancellationToken);
    }

    /// <summary>分页读取已确定的审核分段级失败（§7），按分段 order 升序；limit 范围 1..200。</summary>
    public async Task<ReviewFailurePage> GetFailuresAsync(
        Guid taskId,
        Guid runId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/review-runs/{runId}/failures{PageQuery(limit, cursor)}",
            cancellationToken);
        return await ReadResultAsync<ReviewFailurePage>(response, cancellationToken);
    }

    /// <summary>游标分页查询串：首页只带 limit，翻页附加转义后的不透明游标（§2.5）。</summary>
    private static string PageQuery(int limit, string? cursor)
    {
        return cursor is null
            ? $"?limit={limit}"
            : $"?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";
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

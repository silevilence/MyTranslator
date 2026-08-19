using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 查询运行详情并附带的轮询间隔（§5：调用方应遵守 Retry-After；未返回时轮询间隔不得短于 1 秒）。
/// </summary>
/// <param name="Run">运行快照。</param>
/// <param name="RetryAfterSeconds">响应 Retry-After 表示的秒数；头缺失或不可解析时为 null。</param>
public sealed record TranslationRunPoll(TranslationRun Run, int? RetryAfterSeconds);

/// <summary>
/// AI 翻译运行接口客户端（docs/back/AI 翻译接口约定.md）。
/// 覆盖创建翻译运行、查询运行、列出运行与读取分段级失败。
/// 接口地址经配置 <c>Api:BaseUrl</c> 注入；非成功响应统一抛出
/// <see cref="ApiErrorException"/>，由页面经 <see cref="ApiErrorMessageProvider"/> 映射文案。
/// </summary>
public class TranslationRunService : RunApiClientBase
{
    public TranslationRunService(ApiClient api, IConfiguration config)
        : base(api, config)
    {
    }

    /// <summary>
    /// 创建翻译运行（§4）。<paramref name="sourceLanguage"/> 为 null 或空白表示由 LLM 自动识别；
    /// <paramref name="providerId"/>/<paramref name="modelId"/> 按三档解析（§3.1）：
    /// 都为 null 走默认对，只传 providerId 用该提供商默认模型，都传为精确指定。
    /// 成功后任务进入 <c>processing</c>，返回的运行状态为 <c>queued</c>，
    /// 并附创建响应 Retry-After 作为首轮询间隔参考。
    /// </summary>
    public async Task<TranslationRunPoll> CreateRunAsync(
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl("/api/tasks/")}{taskId}/translation-runs")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await Api.SendAsync(request, cancellationToken);
        var run = await ReadResultAsync<TranslationRun>(response, cancellationToken);
        return new TranslationRunPoll(run, ParseRetryAfter(response.Headers.RetryAfter));
    }

    /// <summary>查询翻译运行（§5）。活动运行响应携带 Retry-After，调用方据此安排轮询。</summary>
    public async Task<TranslationRunPoll> GetRunAsync(
        Guid taskId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/translation-runs/{runId}",
            cancellationToken);
        var run = await ReadResultAsync<TranslationRun>(response, cancellationToken);
        return new TranslationRunPoll(run, ParseRetryAfter(response.Headers.RetryAfter));
    }

    /// <summary>列出当前提取修订的翻译运行（§6），按 createdAt 降序；limit 范围 1..200。</summary>
    public async Task<TranslationRunPage> ListRunsAsync(
        Guid taskId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/translation-runs{PageQuery(limit, cursor)}",
            cancellationToken);
        return await ReadResultAsync<TranslationRunPage>(response, cancellationToken);
    }

    /// <summary>分页读取已确定的分段级失败（§7），按分段 order 升序；limit 范围 1..200。</summary>
    public async Task<TranslationFailurePage> GetFailuresAsync(
        Guid taskId,
        Guid runId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/translation-runs/{runId}/failures{PageQuery(limit, cursor)}",
            cancellationToken);
        return await ReadResultAsync<TranslationFailurePage>(response, cancellationToken);
    }
}

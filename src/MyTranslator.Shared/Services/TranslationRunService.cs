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
public class TranslationRunService
{
    private readonly ApiClient _api;
    private readonly string _baseUrl;

    public TranslationRunService(ApiClient api, IConfiguration config)
    {
        _api = api;
        _baseUrl = config["Api:BaseUrl"] ?? string.Empty;
    }

    /// <summary>拼接接口地址：配置了 <c>Api:BaseUrl</c> 时指向后端，否则使用页面同源相对路径。</summary>
    private string ApiUrl(string path) => _baseUrl + path;

    /// <summary>
    /// 创建翻译运行（§4）。<paramref name="sourceLanguage"/> 为 null 或空白表示由 LLM 自动识别；
    /// 成功后任务进入 <c>processing</c>，返回的运行状态为 <c>queued</c>。
    /// </summary>
    public async Task<TranslationRun> CreateRunAsync(
        Guid taskId,
        int extractionRevision,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(
            new
            {
                extractionRevision,
                sourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage) ? null : sourceLanguage,
                targetLanguage,
            },
            ImportJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl("/api/tasks/")}{taskId}/translation-runs")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await _api.SendAsync(request, cancellationToken);
        return await ReadResultAsync<TranslationRun>(response, cancellationToken);
    }

    /// <summary>查询翻译运行（§5）。活动运行响应携带 Retry-After，调用方据此安排轮询。</summary>
    public async Task<TranslationRunPoll> GetRunAsync(
        Guid taskId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync(
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
        var query = cursor is null
            ? $"?limit={limit}"
            : $"?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";
        using var response = await _api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/translation-runs{query}",
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
        var query = cursor is null
            ? $"?limit={limit}"
            : $"?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";
        using var response = await _api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/translation-runs/{runId}/failures{query}",
            cancellationToken);
        return await ReadResultAsync<TranslationFailurePage>(response, cancellationToken);
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

    /// <summary>
    /// 解析 Retry-After：优先 Delta 秒数，回退 Date 时间点；缺失或无法解析返回 null。
    /// 调用方负责按 §5 将有效轮询间隔下界钳制为 1 秒。
    /// </summary>
    internal static int? ParseRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
        }

        if (retryAfter?.Date is { } date)
        {
            var seconds = (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds);
            return Math.Max(1, seconds);
        }

        return null;
    }
}

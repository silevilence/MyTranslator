using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 运行类接口客户端的公共基座（AI 翻译 / AI 审核共用）：
/// 基础地址拼接（<c>Api:BaseUrl</c>）、游标分页查询串、统一响应读取与 Retry-After 解析。
/// 子类只保留各自资源的端点方法，传输细节单源维护，避免两份客户端逐字重复漂移。
/// </summary>
public abstract class RunApiClientBase
{
    private readonly ApiClient _api;
    private readonly string _baseUrl;

    protected RunApiClientBase(ApiClient api, IConfiguration config)
    {
        _api = api;
        _baseUrl = config["Api:BaseUrl"] ?? string.Empty;
    }
    /// <summary>全局鉴权 HTTP 客户端（自动附加 Bearer 与 401 处理）；子类端点方法直接使用。</summary>
    protected ApiClient Api => _api;

    /// <summary>拼接接口地址：配置了 <c>Api:BaseUrl</c> 时指向后端，否则使用页面同源相对路径。</summary>
    protected string ApiUrl(string path) => _baseUrl + path;

    /// <summary>游标分页查询串：首页只带 limit，翻页附加转义后的不透明游标（§2.5）。</summary>
    protected static string PageQuery(int limit, string? cursor)
    {
        return cursor is null
            ? $"?limit={limit}"
            : $"?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";
    }

    /// <summary>统一读取成功响应；非成功响应抛出 <see cref="ApiErrorException"/> 交由页面映射文案。</summary>
    protected static async Task<T> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
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
    /// 调用方负责将有效轮询间隔下界钳制为 1 秒（§5）。
    /// </summary>
    internal static int? ParseRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return Math.Max((int)Math.Ceiling(delta.TotalSeconds), 1);
        }

        if (retryAfter?.Date is { } date)
        {
            return Math.Max((int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds), 1);
        }

        return null;
    }
}

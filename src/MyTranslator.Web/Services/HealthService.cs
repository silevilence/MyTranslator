using Microsoft.AspNetCore.Components.Authorization;
using MyTranslator.Shared.Services;

namespace MyTranslator.Web.Services;

/// <summary>健康检查结果。</summary>
/// <param name="IsOk">后端连通且返回成功状态。</param>
/// <param name="ErrorMessage">失败时的映射文案（docs/back 错误码映射，经 ApiErrorMessageProvider 生成）。</param>
public sealed record HealthCheckResult(bool IsOk, string? ErrorMessage = null);

/// <summary>
/// 调用后端健康检查接口验证前后端连通。
/// 接口路径经配置（appsettings.json 的 Api:BaseUrl / Api:HealthPath）注入；
/// 契约以 docs/back 鉴权与基础接口约定为准（健康检查为受保护接口，须携带 Token）。
/// </summary>
public class HealthService
{
    private readonly ApiClient _api;
    private readonly ApiErrorMessageProvider _errors;
    private readonly IConfiguration _config;
    private readonly ILogger<HealthService> _logger;

    public HealthService(
        ApiClient api,
        ApiErrorMessageProvider errors,
        IConfiguration config,
        ILogger<HealthService> logger)
    {
        _api = api;
        _errors = errors;
        _config = config;
        _logger = logger;
    }

    /// <summary>执行一次健康检查。</summary>
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var url = BuildHealthUrl();
        try
        {
            using var response = await _api.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode
                ? new HealthCheckResult(true)
                : new HealthCheckResult(false, _errors.For((int)response.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "健康检查请求失败：{Url}", url);
            return new HealthCheckResult(false, _errors.For(null));
        }
    }

    private string BuildHealthUrl()
    {
        var baseUrl = _config["Api:BaseUrl"] ?? string.Empty;
        var healthPath = _config["Api:HealthPath"] ?? "/api/health";
        return baseUrl + healthPath;
    }
}

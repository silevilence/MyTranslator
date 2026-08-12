using Microsoft.AspNetCore.Components.Authorization;
using MyTranslator.Shared.Services;

namespace MyTranslator.Web.Services;

/// <summary>健康检查状态。</summary>
public enum HealthStatus
{
    /// <summary>检查中。</summary>
    Checking,

    /// <summary>后端连通正常。</summary>
    Ok,

    /// <summary>后端不可达或返回非成功状态。</summary>
    Failed,
}

/// <summary>
/// 调用后端健康检查接口验证前后端连通。
/// 接口路径经配置（appsettings.json 的 Api:BaseUrl / Api:HealthPath）注入，
/// 最终契约以后端 docs/back 约定文档为准。
/// </summary>
public class HealthService
{
    private readonly ApiClient _api;
    private readonly IConfiguration _config;
    private readonly ILogger<HealthService> _logger;

    public HealthService(ApiClient api, IConfiguration config, ILogger<HealthService> logger)
    {
        _api = api;
        _config = config;
        _logger = logger;
    }

    /// <summary>执行一次健康检查。</summary>
    public async Task<HealthStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var url = BuildHealthUrl();
        try
        {
            using var response = await _api.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode ? HealthStatus.Ok : HealthStatus.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "健康检查请求失败：{Url}", url);
            return HealthStatus.Failed;
        }
    }

    private string BuildHealthUrl()
    {
        var baseUrl = _config["Api:BaseUrl"] ?? string.Empty;
        var healthPath = _config["Api:HealthPath"] ?? "/api/health";
        return baseUrl + healthPath;
    }
}

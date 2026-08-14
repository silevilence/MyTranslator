using Microsoft.Extensions.Localization;
using MyTranslator.Shared.Resources;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 后端错误码 → 前端文案映射（交互规范 §3）。
/// 映射表依据 docs/back 错误响应约定（HTTP 状态码 + Problem Details）；
/// 页面禁止自行散落硬编码错误文案，一律经本服务取文案。
/// </summary>
public class ApiErrorMessageProvider
{
    private readonly IStringLocalizer<Errors> _localizer;

    public ApiErrorMessageProvider(IStringLocalizer<Errors> localizer)
    {
        _localizer = localizer;
    }

    /// <summary>
    /// 按 HTTP 状态码返回映射文案；<paramref name="status"/> 为 null 表示网络不可达。
    /// </summary>
    public string For(int? status)
    {
        return status switch
        {
            null => _localizer["Network"],
            400 => _localizer["BadRequest"],
            401 => _localizer["Unauthorized"],
            403 => _localizer["Forbidden"],
            404 => _localizer["NotFound"],
            >= 500 => _localizer["ServerError"],
            _ => _localizer["Unknown"],
        };
    }

    /// <summary>
    /// 按 docs/back 错误码优先返回文案（Errors.resx 以错误码为键）；未知错误码降级为状态码映射。
    /// 前端不得自行解释错误码，一律经本方法取文案。
    /// </summary>
    public string ForError(int? status, string? code)
    {
        if (!string.IsNullOrEmpty(code))
        {
            var localized = _localizer[code];
            if (!localized.ResourceNotFound)
            {
                return localized.Value;
            }
        }

        return For(status);
    }

    /// <summary>
    /// 按运行/分段级失败码返回文案（§10.2 异步失败码）；失败码不绑定 HTTP 状态，
    /// 未知失败码降级为通用失败文案而非网络错误文案。
    /// </summary>
    public string ForFailureCode(string? code)
    {
        if (!string.IsNullOrEmpty(code))
        {
            var localized = _localizer[code];
            if (!localized.ResourceNotFound)
            {
                return localized.Value;
            }
        }

        return _localizer["Unknown"];
    }

    /// <summary>按 <see cref="ApiErrorException"/> 的 code/状态码返回映射文案。</summary>
    public string ForError(ApiErrorException exception)
    {
        return ForError(exception.StatusCode, exception.Code);
    }

    /// <summary>
    /// 按异常类型返回映射文案：<see cref="ApiErrorException"/> 按 code 映射；
    /// <see cref="HttpRequestException"/> 视为网络不可达；其余按未知处理。
    /// 页面捕获异常统一经此方法取文案，避免各页重复编写捕获形状。
    /// </summary>
    public string ForException(Exception exception)
    {
        return exception switch
        {
            ApiErrorException api => ForError(api),
            HttpRequestException => For(null),
            _ => For(null),
        };
    }
}

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
}

using Microsoft.Extensions.Localization;
using MudBlazor;
using MyTranslator.Shared.Resources;

namespace MyTranslator.Shared.Services;

/// <summary>统一通用控件的中文提示与无障碍名称，避免回退成 Close、Browse files 等英文。</summary>
public sealed class ChineseMudLocalizer(IStringLocalizer<MudStrings> localizer) : MudLocalizer
{
    public override LocalizedString this[string key] => localizer[key];
    public override LocalizedString this[string key, params object[] arguments] => localizer[key, arguments];
}

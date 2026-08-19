using MudBlazor;

namespace MyTranslator.Shared.Components;

/// <summary>
/// 运行状态 → 视觉映射的唯一来源（文字 + 语义色 + 图标，三通道一致）。
/// 翻译与审核运行共用同一状态机（queued/processing/completed/partial_failed/failed），
/// 进度条颜色与状态徽标共用本映射，避免各面板重复 switch 漂移（审查标准维度二：语义色按映射表使用）。
/// 未知状态容忍：未知状态但已终态（finishedAt 非空）按中性处理；未知活动状态按进行中（Info）处理（§2.1 容忍未知枚举值）。
/// </summary>
public static class RunStatusVisual
{
    /// <summary>运行状态徽标文案键（RunPanelStrings resx）；与 <see cref="Visuals"/> 一一对应。</summary>
    public static (string TextKey, Color Color, string Icon) Resolve(string? status, bool finished) => status switch
    {
        "queued" => ("RunQueued", Color.Default, Icons.Material.Filled.Schedule),
        "processing" => ("RunProcessing", Color.Info, Icons.Material.Filled.Sync),
        "completed" => ("RunCompleted", Color.Success, Icons.Material.Filled.CheckCircle),
        "partial_failed" => ("RunPartialFailed", Color.Error, Icons.Material.Filled.WarningAmber),
        "failed" => ("RunFailed", Color.Error, Icons.Material.Filled.ErrorOutline),
        _ when finished => ("RunUnknown", Color.Default, Icons.Material.Filled.HelpOutline),
        _ => ("RunUnknown", Color.Info, Icons.Material.Filled.HelpOutline),
    };

    /// <summary>未知状态兜底：文本恒可用，避免局部化缺失时空白。</summary>
    public static string FallbackText() => "Unknown";
}

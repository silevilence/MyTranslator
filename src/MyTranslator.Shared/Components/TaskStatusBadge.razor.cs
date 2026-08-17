using MudBlazor;

namespace MyTranslator.Shared.Components;

/// <summary>
/// 任务状态徽标共享逻辑：任务公共状态 → 语义色映射的唯一来源（交互规范 §4），
/// 供徽标组件与页面内进度条等呈现复用，避免映射散落重复。
/// </summary>
public sealed partial class TaskStatusBadge
{
    /// <summary>任务公共状态（created/processing/completed/failed）→ 语义色；未知状态取中性色。</summary>
    public static Color SemanticColor(string? status) => status switch
    {
        "processing" => Color.Info,
        "completed" => Color.Success,
        "failed" => Color.Error,
        _ => Color.Default,
    };
}

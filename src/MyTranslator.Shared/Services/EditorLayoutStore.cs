using System.Text.Json;
using Microsoft.JSInterop;

namespace MyTranslator.Shared.Services;

/// <summary>编辑器分栏布局的取值边界（设计规范 §4.3）。</summary>
public static class SplitLayout
{
    /// <summary>分割比例下限；列表列不得窄于此值。</summary>
    public const double MinRatio = 0.2;

    /// <summary>分割比例上限；详情列不得窄于此值。</summary>
    public const double MaxRatio = 0.8;

    /// <summary>未持久化或记录损坏时的默认比例。</summary>
    public const double DefaultRatio = 0.5;

    /// <summary>把任意输入收敛到合法区间。</summary>
    public static double Clamp(double ratio) => double.IsFinite(ratio)
        ? Math.Clamp(ratio, MinRatio, MaxRatio)
        : DefaultRatio;

    /// <summary>紧凑断点（1024–1280px）下详情面板下叠，不显示拖拽分栏（设计规范 §4.1）。</summary>
    public static bool IsStacked(MudBlazor.Breakpoint breakpoint) => breakpoint
        is MudBlazor.Breakpoint.Xs or MudBlazor.Breakpoint.Sm or MudBlazor.Breakpoint.Md;
}

/// <summary>
/// 编辑器分栏比例与折叠状态的本地持久化（设计规范 §4.3），经 wwwroot/js/storage.js 桥存 localStorage。
/// 偏好属于用户而非任务，全局一个键；损坏记录按默认值处理，不阻断编辑器。
/// </summary>
public static class EditorLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>读取分栏布局；无记录或记录损坏时返回默认比例、不折叠。</summary>
    public static async Task<(double Ratio, bool Collapsed)> LoadAsync(IJSRuntime js)
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("mtStorage.getItem", Key);
            if (string.IsNullOrEmpty(raw))
            {
                return (SplitLayout.DefaultRatio, false);
            }

            var record = JsonSerializer.Deserialize<Record>(raw, JsonOptions);
            return record is null
                ? (SplitLayout.DefaultRatio, false)
                : (SplitLayout.Clamp(record.Ratio), record.Collapsed);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return (SplitLayout.DefaultRatio, false);
        }
    }

    /// <summary>持久化分栏布局；比例夹取到合法区间。</summary>
    public static Task SaveAsync(IJSRuntime js, double ratio, bool collapsed)
    {
        var record = new Record { Ratio = SplitLayout.Clamp(ratio), Collapsed = collapsed };
        return js.InvokeVoidAsync("mtStorage.setItem", Key, JsonSerializer.Serialize(record, JsonOptions)).AsTask();
    }

    /// <summary>持久化形状：仅比例与折叠标记。</summary>
    private sealed record Record
    {
        public double Ratio { get; init; }

        public bool Collapsed { get; init; }
    }

    private const string Key = "mt.editorLayout";
}

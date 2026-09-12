using Microsoft.JSInterop;
using MyTranslator.Shared.Services;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 编辑器分栏布局持久化与夹取规则（设计规范 §4.3）：比例夹取到 20%–80%、
/// 折叠状态持久化、损坏记录按默认值处理。存储经 wwwroot/js/storage.js 桥，用内存替身模拟。
/// </summary>
public sealed class EditorLayoutStoreTests
{
    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 0.2)]
    [InlineData(1.0, 0.8)]
    [InlineData(-3, 0.2)]
    [InlineData(double.NaN, 0.5)]
    public void ClampKeepsRatioInDocumentedRange(double input, double expected)
    {
        Assert.Equal(expected, SplitLayout.Clamp(input));
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsRatioAndCollapsed()
    {
        var js = new StorageFake();
        await EditorLayoutStore.SaveAsync(js, 0.7, collapsed: true);
        var (ratio, collapsed) = await EditorLayoutStore.LoadAsync(js);
        Assert.Equal(0.7, ratio);
        Assert.True(collapsed);

        // 保存时越界比例同样夹取，读回值与合法区间一致
        await EditorLayoutStore.SaveAsync(js, 5, collapsed: false);
        var (clamped, collapsedAfter) = await EditorLayoutStore.LoadAsync(js);
        Assert.Equal(SplitLayout.MaxRatio, clamped);
        Assert.False(collapsedAfter);
    }

    [Fact]
    public async Task MissingOrCorruptRecordFallsBackToDefaults()
    {
        var js = new StorageFake();
        var defaults = await EditorLayoutStore.LoadAsync(js);
        Assert.Equal(SplitLayout.DefaultRatio, defaults.Ratio);
        Assert.False(defaults.Collapsed);

        await js.SetRawAsync("mt.editorLayout", "{not json");
        var corrupt = await EditorLayoutStore.LoadAsync(js);
        Assert.Equal(SplitLayout.DefaultRatio, corrupt.Ratio);
        Assert.False(corrupt.Collapsed);
    }

    [Theory]
    [InlineData(MudBlazor.Breakpoint.Xs, true)]
    [InlineData(MudBlazor.Breakpoint.Sm, true)]
    [InlineData(MudBlazor.Breakpoint.Md, true)]
    [InlineData(MudBlazor.Breakpoint.Lg, false)]
    [InlineData(MudBlazor.Breakpoint.Xl, false)]
    public void CompactBreakpointsStackDetailPane(MudBlazor.Breakpoint breakpoint, bool stacked)
    {
        Assert.Equal(stacked, SplitLayout.IsStacked(breakpoint));
    }

    /// <summary>mtStorage 桥的内存替身：仅支持 getItem/setItem。</summary>
    private sealed class StorageFake : IJSRuntime
    {
        private readonly Dictionary<string, string> _values = [];

        public Task SetRawAsync(string key, string value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            switch (identifier)
            {
                case "mtStorage.getItem":
                    return ValueTask.FromResult(_values.TryGetValue(Assert.IsType<string>(args![0]), out var value)
                        ? (TValue)(object)value
                        : default(TValue)!);
                case "mtStorage.setItem":
                    _values[(string)args![0]!] = (string)args[1]!;
                    return default;
                default:
                    throw new NotSupportedException($"Unexpected JS interop call: {identifier}");
            }
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }
}

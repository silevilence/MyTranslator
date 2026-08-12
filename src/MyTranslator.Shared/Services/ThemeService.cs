using Microsoft.JSInterop;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 亮/暗主题状态（设计规范 §2.1）：应用内手动切换并持久化到 localStorage；
/// 系统暗色模式跟随等非用户操作仅同步内存状态，不写入存储。
/// 每端根组件（App.razor）订阅 <see cref="Changed"/> 事件驱动 MudThemeProvider。
/// </summary>
public class ThemeService
{
    private readonly IJSRuntime _js;
    private bool _isDark;

    public ThemeService(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>当前是否暗色模式。</summary>
    public bool IsDark => _isDark;

    /// <summary>主题变化事件（订阅者应触发重渲染）。</summary>
    public event EventHandler? Changed;

    /// <summary>从本地存储恢复用户上次选择（启动时调用一次）。</summary>
    public async Task InitializeAsync()
    {
        _isDark = await _js.InvokeAsync<bool?>("mtStorage.getDarkMode") ?? false;
    }

    /// <summary>切换主题并持久化（仅用户显式操作调用）。</summary>
    public async Task ToggleAsync()
    {
        await SetDarkAsync(!_isDark);
    }

    /// <summary>设置主题并持久化（仅用户显式操作调用）。</summary>
    public async Task SetDarkAsync(bool dark)
    {
        if (_isDark == dark)
        {
            return;
        }

        _isDark = dark;
        await _js.InvokeVoidAsync("mtStorage.setDarkMode", dark);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 仅同步内存状态，不持久化（MudThemeProvider 系统暗色跟随等非用户来源变化走此路径）。
    /// </summary>
    public void ApplyDark(bool dark)
    {
        if (_isDark == dark)
        {
            return;
        }

        _isDark = dark;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

using Microsoft.JSInterop;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class ThemeServiceTests
{
    [Fact]
    public async Task ToggleAsync_切换并持久化()
    {
        var js = new FakeJSRuntime();
        var service = new ThemeService(js);
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.ToggleAsync();

        Assert.True(service.IsDark);
        Assert.Equal(true, js.GetValue("mt.darkMode"));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task SetDarkAsync_相同值不重复写入()
    {
        var js = new FakeJSRuntime();
        var service = new ThemeService(js);
        var changed = 0;
        service.Changed += (_, _) => changed++;

        await service.SetDarkAsync(false);

        Assert.Equal(0, changed);
        Assert.Null(js.GetValue("mt.darkMode"));
    }

    [Fact]
    public async Task ApplyDark_仅同步状态不持久化()
    {
        var js = new FakeJSRuntime();
        var service = new ThemeService(js);

        service.ApplyDark(true);

        Assert.True(service.IsDark);
        Assert.Null(js.GetValue("mt.darkMode"));
    }

    [Fact]
    public async Task InitializeAsync_读取已持久化偏好()
    {
        var js = new FakeJSRuntime();
        await js.InvokeVoidAsync("mtStorage.setDarkMode", true);
        var service = new ThemeService(js);

        await service.InitializeAsync();

        Assert.True(service.IsDark);
    }
}

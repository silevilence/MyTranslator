using Microsoft.AspNetCore.Components;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 内存版 NavigationManager：记录 NavigateTo 调用，供 ApiClient 401 跳转断言使用。
/// </summary>
internal sealed class FakeNavigationManager : NavigationManager
{
    public FakeNavigationManager(string uri = "http://localhost/")
    {
        Initialize("http://localhost/", uri);
    }

    public List<string> Navigations { get; } = [];

    public string? LastNavigation => Navigations.LastOrDefault();

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        Navigations.Add(uri);
    }
}

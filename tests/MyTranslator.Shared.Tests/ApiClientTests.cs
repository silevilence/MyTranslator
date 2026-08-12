using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class ApiClientTests
{
    private static (ApiClient Client, FakeJSRuntime Js, FakeNavigationManager Nav, StubHttpMessageHandler Handler) Create(
        HttpStatusCode statusCode,
        string currentUri = "http://localhost/")
    {
        var js = new FakeJSRuntime();
        var nav = new FakeNavigationManager(currentUri);
        var handler = new StubHttpMessageHandler(statusCode);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var store = new TokenStore(js);
        var auth = new AuthStateProvider(store);
        var client = new ApiClient(http, auth, nav, NullLogger<ApiClient>.Instance);
        return (client, js, nav, handler);
    }

    [Fact]
    public async Task SendAsync_附加Bearer鉴权头()
    {
        var (client, js, _, handler) = Create(HttpStatusCode.OK);
        await js.InvokeVoidAsync("mtStorage.setToken", "sk-dev-test");

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(
            new AuthenticationHeaderValue("Bearer", "sk-dev-test"),
            sent.Headers.Authorization);
    }

    [Fact]
    public async Task SendAsync_无Token时不附加鉴权头()
    {
        var (client, _, _, handler) = Create(HttpStatusCode.OK);

        await client.GetAsync("/api/health");

        var sent = Assert.Single(handler.Requests);
        Assert.Null(sent.Headers.Authorization);
    }

    [Fact]
    public async Task SendAsync_401时清理Token并跳转登录带回跳地址()
    {
        var (client, js, nav, _) = Create(HttpStatusCode.Unauthorized, "http://localhost/tasks/42");
        await js.InvokeVoidAsync("mtStorage.setToken", "sk-dev-expired");

        using var response = await client.GetAsync("/api/tasks/42");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(js.IsRemoved("mt.token"));
        Assert.Equal("/login?returnUrl=tasks%2F42", nav.LastNavigation);
    }

    [Fact]
    public async Task SendAsync_已在登录页时401不重复跳转()
    {
        var (client, js, nav, _) = Create(HttpStatusCode.Unauthorized, "http://localhost/login?returnUrl=tasks");
        await js.InvokeVoidAsync("mtStorage.setToken", "sk-dev-expired");

        await client.GetAsync("/api/tokens");

        Assert.Empty(nav.Navigations);
        // 登录页自身不清理已输入 Token：401 清理交由受保护页面的请求触发
        Assert.False(js.IsRemoved("mt.token"));
    }

    [Fact]
    public async Task SendAsync_非401状态不登出不跳转()
    {
        var (client, js, nav, _) = Create(HttpStatusCode.InternalServerError, "http://localhost/tasks");
        await js.InvokeVoidAsync("mtStorage.setToken", "sk-dev-test");

        using var response = await client.GetAsync("/api/tasks");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(nav.Navigations);
        Assert.False(js.IsRemoved("mt.token"));
    }
}

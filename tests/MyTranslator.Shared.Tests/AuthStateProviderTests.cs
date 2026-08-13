using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MyTranslator.Shared.Services;
using Xunit;

namespace MyTranslator.Shared.Tests;

public class AuthStateProviderTests
{
    private static (AuthStateProvider Auth, FakeJSRuntime Js) Create()
    {
        var js = new FakeJSRuntime();
        var auth = new AuthStateProvider(new TokenStore(js));
        return (auth, js);
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_无Token为未认证()
    {
        var (auth, _) = Create();

        var state = await auth.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_有Token为已认证()
    {
        var (auth, js) = Create();
        await js.InvokeVoidAsync("mtStorage.setToken", "sk-dev-test");

        var state = await auth.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal("Bearer", state.User.Identity?.AuthenticationType);
    }

    [Fact]
    public async Task LoginAsync_保存Token并广播认证状态()
    {
        var (auth, js) = Create();
        var notified = false;
        auth.AuthenticationStateChanged += _ => notified = true;

        await auth.LoginAsync("sk-dev-new");

        Assert.Equal("sk-dev-new", js.GetValue("mt.token"));
        Assert.True(notified);
        var state = await auth.GetAuthenticationStateAsync();
        Assert.True(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task LogoutAsync_清除Token并广播未认证状态()
    {
        var (auth, js) = Create();
        await js.InvokeVoidAsync("mtStorage.setToken", "sk-dev-test");
        var notified = false;
        auth.AuthenticationStateChanged += _ => notified = true;

        await auth.LogoutAsync();

        Assert.True(js.IsRemoved("mt.token"));
        Assert.True(notified);
        var state = await auth.GetAuthenticationStateAsync();
        Assert.False(state.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task GetTokenAsync_返回当前Token()
    {
        var (auth, js) = Create();
        await js.InvokeVoidAsync("mtStorage.setToken", "sk-dev-test");

        Assert.Equal("sk-dev-test", await auth.GetTokenAsync());
    }
}

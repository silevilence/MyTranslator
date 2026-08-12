using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 基于本地 Token 的认证状态提供器。Token 存在即视为已认证；
/// 登录/登出后通过 <see cref="NotifyAuthenticationStateChanged"/> 驱动路由守卫刷新。
/// </summary>
public class AuthStateProvider : AuthenticationStateProvider
{
    public const string AuthenticationScheme = "Bearer";

    private readonly TokenStore _store;

    public AuthStateProvider(TokenStore store)
    {
        _store = store;
    }

    /// <summary>当前持久化的 Token（供 ApiClient 附加鉴权头）。</summary>
    public async Task<string?> GetTokenAsync()
    {
        return await _store.GetAsync();
    }

    /// <summary>保存 Token 并广播认证状态变化。</summary>
    public async Task LoginAsync(string token)
    {
        await _store.SaveAsync(token);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    /// <summary>清除 Token 并广播认证状态变化（401 自动登出复用此方法）。</summary>
    public async Task LogoutAsync()
    {
        await _store.ClearAsync();
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await _store.GetAsync();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Authentication, token)],
            AuthenticationScheme);
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }
}

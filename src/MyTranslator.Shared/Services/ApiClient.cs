using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 全局 HTTP 客户端：自动附加 `Authorization: Bearer {Token}`；
/// 任意请求返回 401 时自动清理 Token、通知认证状态并跳转登录页。
/// 页面统一经本客户端发请求，禁止直接使用裸 HttpClient。
/// </summary>
public class ApiClient
{
    private readonly HttpClient _http;
    private readonly AuthStateProvider _auth;
    private readonly NavigationManager _navigation;
    private readonly ILogger<ApiClient> _logger;

    public ApiClient(
        HttpClient http,
        AuthStateProvider auth,
        NavigationManager navigation,
        ILogger<ApiClient> logger)
    {
        _http = http;
        _auth = auth;
        _navigation = navigation;
        _logger = logger;
    }

    /// <summary>发起 GET 请求（自动鉴权头 + 401 处理）。</summary>
    public async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync(request, cancellationToken);
    }

    /// <summary>发起任意请求（自动鉴权头 + 401 处理）。</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var token = await _auth.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
        }

        return response;
    }

    private async Task HandleUnauthorizedAsync()
    {
        var currentPath = _navigation.ToBaseRelativePath(_navigation.Uri);
        var currentPage = currentPath.Split('?', '#')[0];
        if (currentPage.Equals("login", StringComparison.OrdinalIgnoreCase) ||
            currentPage.StartsWith("login/", StringComparison.OrdinalIgnoreCase))
        {
            return; // 已在登录页，避免重定向循环
        }

        _logger.LogWarning("API 返回 401，Token 已失效：清理并跳转登录");
        await _auth.LogoutAsync();
        var target = string.IsNullOrEmpty(currentPath)
            ? "/login"
            : $"/login?returnUrl={Uri.EscapeDataString(currentPath)}";
        _navigation.NavigateTo(target);
    }
}

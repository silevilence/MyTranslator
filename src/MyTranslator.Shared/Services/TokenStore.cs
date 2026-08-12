using Microsoft.JSInterop;

namespace MyTranslator.Shared.Services;

/// <summary>
/// Token 本地持久化（localStorage）。Web 与桌面端共用同一存储桥（wwwroot/js/storage.js）。
/// </summary>
public class TokenStore
{
    private readonly IJSRuntime _js;

    public TokenStore(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>读取已保存 Token；未保存返回 null。</summary>
    public async Task<string?> GetAsync()
    {
        return await _js.InvokeAsync<string?>("mtStorage.getToken");
    }

    /// <summary>保存 Token 到本地持久化。</summary>
    public async Task SaveAsync(string token)
    {
        await _js.InvokeVoidAsync("mtStorage.setToken", token);
    }

    /// <summary>清除本地持久化 Token。</summary>
    public async Task ClearAsync()
    {
        await _js.InvokeVoidAsync("mtStorage.clearToken");
    }
}

using Microsoft.JSInterop;

namespace MyTranslator.Shared.Tests;

/// <summary>
/// 内存版 IJSRuntime：模拟 wwwroot/js/storage.js 的 mtStorage 桥，供无浏览器单元测试使用。
/// </summary>
internal sealed class FakeJSRuntime : IJSRuntime
{
    private readonly Dictionary<string, object?> _values = [];
    private readonly HashSet<string> _removed = [];

    public object? GetValue(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public bool IsRemoved(string key) => _removed.Contains(key);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        switch (identifier)
        {
            case "mtStorage.getToken":
                return Result<TValue>(GetValue("mt.token") as string);
            case "mtStorage.getDarkMode":
                return Result<TValue>(GetValue("mt.darkMode"));
            case "mtStorage.setToken":
                _values["mt.token"] = args?[0];
                return default;
            case "mtStorage.setDarkMode":
                _values["mt.darkMode"] = args?[0];
                return default;
            case "mtStorage.clearToken":
                _removed.Add("mt.token");
                _values.Remove("mt.token");
                return default;
            default:
                throw new NotSupportedException($"Unexpected JS interop call: {identifier}");
        }
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => InvokeAsync<TValue>(identifier, args);

    private static ValueTask<TValue> Result<TValue>(object? value)
        => new((TValue)value!);
}

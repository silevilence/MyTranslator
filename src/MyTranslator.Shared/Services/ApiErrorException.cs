using System.Text.Json;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 后端非成功响应的类型化异常：携带 HTTP 状态码与 Problem Details 的 `code` / `errors` 扩展
/// （docs/back 错误响应约定）。页面一律经 <see cref="ApiErrorMessageProvider"/> 将
/// <see cref="Code"/> 映射为文案，禁止自行解释错误码。
/// </summary>
public sealed class ApiErrorException : Exception
{
    public ApiErrorException(
        int statusCode,
        string? code,
        string? instance,
        IReadOnlyDictionary<string, JsonElement>? errors,
        string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Instance = instance;
        Errors = errors;
    }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>Problem Details `code` 字段；响应非 Problem Details 时为 null。</summary>
    public string? Code { get; }

    /// <summary>Problem Details `instance` 字段（请求路径），仅用于诊断。</summary>
    public string? Instance { get; }

    /// <summary>Problem Details `errors` 扩展的原始字段；无扩展时为 null。</summary>
    public IReadOnlyDictionary<string, JsonElement>? Errors { get; }

    /// <summary>读取 <see cref="Errors"/> 中的整数项（如重新提取 409 的 translatedSegments / confirmedSegments）。</summary>
    public int? GetErrorInt(string key)
    {
        if (Errors is null || !Errors.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    }

    /// <summary>读取 <see cref="Errors"/> 中的字符串项（如术语 409 term_conflict 的 conflictingTermId）。</summary>
    public string? GetErrorString(string key)
    {
        if (Errors is null || !Errors.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// 从非成功响应构造异常：尽力解析 Problem Details 的 `code` / `instance` / `errors`；
    /// 响应体不可解析为 JSON 时降级为仅状态码（不阻断错误处理）。
    /// </summary>
    public static async Task<ApiErrorException> FromResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        var statusCode = (int)response.StatusCode;
        string? code = null;
        string? instance = null;
        IReadOnlyDictionary<string, JsonElement>? errors = null;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var root = document.RootElement;
                code = ReadString(root, "code");
                instance = ReadString(root, "instance");
                if (root.TryGetProperty("errors", out var errorObject)
                    && errorObject.ValueKind == JsonValueKind.Object)
                {
                    errors = errorObject.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value.Clone());
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            // 非 Problem Details 响应：仅携带状态码即可
        }

        return new ApiErrorException(
            statusCode,
            code,
            instance,
            errors,
            $"API 请求失败：HTTP {statusCode}" + (code is null ? string.Empty : $"，code={code}"));
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}

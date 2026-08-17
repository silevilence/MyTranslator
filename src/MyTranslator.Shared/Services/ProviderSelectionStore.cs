using System.Text.Json;
using Microsoft.JSInterop;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 翻译面板提供商/模型选择的本地持久化（按任务维度存 localStorage，经 wwwroot/js/storage.js 桥）。
/// 页面刷新后恢复上次选择（AI 翻译接口约定 §11.1 第 6 步：不依赖仅存内存的状态）；
/// 恢复时调用方须校验引用的提供商/模型仍存在于当前配置。
/// </summary>
public static class ProviderSelectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>读取任务的持久化选择；无记录或解析失败返回双 null（表示走默认对）。</summary>
    public static async Task<(Guid? ProviderId, Guid? ModelId)> LoadAsync(
        IJSRuntime js,
        Guid taskId)
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("mtStorage.getItem", $"mt.providerSelection.{taskId:N}");
            if (string.IsNullOrEmpty(raw))
            {
                return (null, null);
            }

            var record = JsonSerializer.Deserialize<Record>(raw, JsonOptions);
            return record is null ? (null, null) : (record.ProviderId, record.ModelId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // 损坏记录按未选择处理，不阻断面板
            return (null, null);
        }
    }

    /// <summary>持久化任务的选择；双 null 表示清除记录（回默认对）。</summary>
    public static async Task SaveAsync(
        IJSRuntime js,
        Guid taskId,
        Guid? providerId,
        Guid? modelId)
    {
        var key = $"mt.providerSelection.{taskId:N}";
        if (providerId is null && modelId is null)
        {
            await js.InvokeVoidAsync("mtStorage.removeItem", key);
            return;
        }

        var record = new Record { ProviderId = providerId, ModelId = modelId };
        await js.InvokeVoidAsync("mtStorage.setItem", key, JsonSerializer.Serialize(record, JsonOptions));
    }

    /// <summary>持久化形状：仅两个 ID，不含密钥或配置内容。</summary>
    private sealed record Record
    {
        public Guid? ProviderId { get; init; }

        public Guid? ModelId { get; init; }
    }
}

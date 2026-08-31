using System.Text.Json;
using Microsoft.JSInterop;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 历史翻译对比语言对的本地持久化（按任务维度存 localStorage，经 wwwroot/js/storage.js 桥）。
/// 对比接口（docs/back/TM 接口约定.md §6.1）要求调用方提交明确源/目标语言；
/// 复用翻译面板的按任务维度持久化约定（<see cref="ProviderSelectionStore"/>：刷新后恢复上次输入）。
/// </summary>
public static class TaskLanguagePairStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>读取任务的语言对；无记录、记录损坏时返回双 null 的 <see cref="LanguagePair"/>。</summary>
    public static async Task<LanguagePair> LoadAsync(IJSRuntime js, Guid taskId)
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("mtStorage.getItem", $"mt.languagePair.{taskId:N}");
            if (string.IsNullOrEmpty(raw))
            {
                return new LanguagePair(null, null);
            }

            var record = JsonSerializer.Deserialize<Record>(raw, JsonOptions);
            return record is null
                ? new LanguagePair(null, null)
                : LanguagePair.Normalize(record.SourceLanguage, record.TargetLanguage);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // 损坏记录按未设置处理，不阻断面板
            return new LanguagePair(null, null);
        }
    }

    /// <summary>持久化任务的语言对；规范化后双 null 时清除记录。</summary>
    public static async Task SaveAsync(IJSRuntime js, Guid taskId, LanguagePair pair)
    {
        var normalized = LanguagePair.Normalize(pair.SourceLanguage, pair.TargetLanguage);
        var key = $"mt.languagePair.{taskId:N}";
        if (normalized.SourceLanguage is null && normalized.TargetLanguage is null)
        {
            await js.InvokeVoidAsync("mtStorage.removeItem", key);
            return;
        }

        var record = new Record
        {
            SourceLanguage = normalized.SourceLanguage,
            TargetLanguage = normalized.TargetLanguage,
        };
        await js.InvokeVoidAsync("mtStorage.setItem", key, JsonSerializer.Serialize(record, JsonOptions));
    }

    /// <summary>持久化形状：仅两个语言标签，不含其他内容。</summary>
    private sealed record Record
    {
        public string? SourceLanguage { get; init; }

        public string? TargetLanguage { get; init; }
    }
}

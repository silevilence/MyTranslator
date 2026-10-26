using System.Text.Json;
using MyTranslator.Api.FileTasks;

namespace MyTranslator.Api.Rules;

/// <summary>字幕语法约束独立于运行编排；占位符完整性由另一条规则检查。</summary>
public sealed class SubtitleBodyRule : ITranslationRule
{
    public string Id => "subtitle_structure";

    public TranslationRuleViolation? Evaluate(TranslationRuleContext context)
    {
        if (context.FileType is not ("srt" or "vtt") || context.TargetText is null) return null;
        var text = context.TargetText;
        using var table = JsonDocument.Parse(context.MarkupTableJson);
        foreach (var item in table.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetInt32();
            // 用非空哨兵替换引用，保留独占一行的受保护实体/样式的合法性。
            text = text.Replace($"<x{id}>", "M", StringComparison.Ordinal)
                .Replace($"</x{id}>", "M", StringComparison.Ordinal)
                .Replace($"<x{id}/>", "M", StringComparison.Ordinal);
        }
        return SubtitleExtraction.IsPlainBodyValid(text) ? null : new("subtitle_structure_violation", true);
    }
}

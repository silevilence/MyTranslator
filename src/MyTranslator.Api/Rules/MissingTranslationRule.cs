using MyTranslator.Api.TranslationMemory;

namespace MyTranslator.Api.Rules;

/// <summary>只判断确定性的缺失：null、空白或仅剩注册标记；相同原译文可能是专名，不推测为漏翻。</summary>
public sealed class MissingTranslationRule : ITranslationRule
{
    public string Id => "missing_translation";

    public TranslationRuleViolation? Evaluate(TranslationRuleContext context) =>
        string.IsNullOrWhiteSpace(context.TargetText) ||
        string.IsNullOrWhiteSpace(TranslationMemoryMarkup.RemoveRegisteredReferences(context.TargetText, context.MarkupTableJson))
            ? new("missing_translation", false, 0, context.TargetText?.Length ?? 0)
            : null;
}

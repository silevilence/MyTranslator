namespace MyTranslator.Api.Rules;

public interface ITranslationRule
{
    TranslationRuleViolation? Evaluate(TranslationRuleContext context);
}

public sealed record TranslationRuleContext(
    Guid SegmentId,
    string SourceText,
    string TargetText,
    string MarkupTableJson);

public sealed record TranslationRuleViolation(string Code, bool Retryable);

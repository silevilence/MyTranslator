using System.Text.Json;
using MyTranslator.Api.Text;

namespace MyTranslator.Api.Rules;

public sealed class PlaceholderIntegrityRule : ITranslationRule
{
    public TranslationRuleViolation? Evaluate(TranslationRuleContext context) =>
        IsSatisfied(context.SourceText, context.TargetText, context.MarkupTableJson)
            ? null
            : new TranslationRuleViolation("placeholder_integrity_violation", true);

    private static bool IsSatisfied(string sourceText, string targetText, string markupTableJson)
    {
        using var markup = JsonDocument.Parse(markupTableJson);
        var requiredTokens = PlaceholderReferences.RequiredReferences(
            markup.RootElement.EnumerateArray().Select(PlaceholderReferences.DeclarationOf));
        var knownTokens = requiredTokens.ToHashSet(StringComparer.Ordinal);
        var sourceReferences = PlaceholderReferences.RegisteredReferences(sourceText, knownTokens);
        var targetReferences = PlaceholderReferences.RegisteredReferences(targetText, knownTokens);

        return PlaceholderReferences.HaveSameCounts(sourceReferences, requiredTokens) &&
               sourceReferences.SequenceEqual(targetReferences, StringComparer.Ordinal) &&
               PlaceholderReferences.HaveSameCounts(
                   PlaceholderReferences.UnregisteredReferences(sourceText, knownTokens),
                   PlaceholderReferences.UnregisteredReferences(targetText, knownTokens));
    }
}

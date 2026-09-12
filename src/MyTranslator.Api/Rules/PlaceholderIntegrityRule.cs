using System.Text.Json;
using MyTranslator.Api.Text;

namespace MyTranslator.Api.Rules;

public sealed class PlaceholderIntegrityRule : ITranslationRule
{
    public string Id => "placeholder_integrity";

    public TranslationRuleViolation? Evaluate(TranslationRuleContext context) =>
        context.TargetText is null || IsSatisfied(context.SourceText, context.TargetText, context.MarkupTableJson)
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

        return CorrectlyNested(sourceReferences) &&
               PlaceholderReferences.HaveSameCounts(sourceReferences, requiredTokens) &&
               sourceReferences.SequenceEqual(targetReferences, StringComparer.Ordinal) &&
               PlaceholderReferences.HaveSameCounts(
                   PlaceholderReferences.UnregisteredReferences(sourceText, knownTokens),
                   PlaceholderReferences.UnregisteredReferences(targetText, knownTokens));
    }

    private static bool CorrectlyNested(IEnumerable<string> references)
    {
        var stack = new Stack<string>();
        foreach (var reference in references)
        {
            if (reference.StartsWith("</", StringComparison.Ordinal))
            {
                if (!stack.TryPop(out var opening) || opening != reference.Replace("</", "<", StringComparison.Ordinal))
                    return false;
            }
            else if (!reference.EndsWith("/>", StringComparison.Ordinal))
                stack.Push(reference);
        }
        return stack.Count == 0;
    }
}

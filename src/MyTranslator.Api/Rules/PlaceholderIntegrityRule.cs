using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyTranslator.Api.Rules;

public sealed partial class PlaceholderIntegrityRule : ITranslationRule
{
    public TranslationRuleViolation? Evaluate(TranslationRuleContext context) =>
        IsSatisfied(context.SourceText, context.TargetText, context.MarkupTableJson)
            ? null
            : new TranslationRuleViolation("placeholder_integrity_violation", true);

    private static bool IsSatisfied(string sourceText, string targetText, string markupTableJson)
    {
        using var markup = JsonDocument.Parse(markupTableJson);
        var knownTokens = new HashSet<string>(StringComparer.Ordinal);
        var requiredTokens = new List<string>();
        foreach (var item in markup.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetInt32();
            if (item.GetProperty("kind").GetString() == "paired")
            {
                requiredTokens.Add($"<x{id}>");
                requiredTokens.Add($"</x{id}>");
            }
            else
            {
                requiredTokens.Add($"<x{id}/>");
            }
        }

        knownTokens.UnionWith(requiredTokens);
        var sourceTokens = PlaceholderPattern().Matches(sourceText).Select(match => match.Value).ToArray();
        var targetTokens = PlaceholderPattern().Matches(targetText).Select(match => match.Value).ToArray();
        var sourceReferences = sourceTokens.Where(knownTokens.Contains).ToArray();
        var targetReferences = targetTokens.Where(knownTokens.Contains).ToArray();

        return HaveSameCounts(sourceReferences, requiredTokens) &&
               sourceReferences.SequenceEqual(targetReferences, StringComparer.Ordinal) &&
               HaveSameCounts(
                   sourceTokens.Where(token => !knownTokens.Contains(token)),
                   targetTokens.Where(token => !knownTokens.Contains(token)));
    }

    private static bool HaveSameCounts(IEnumerable<string> left, IEnumerable<string> right)
    {
        var leftCounts = left.GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var rightCounts = right.GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return leftCounts.Count == rightCounts.Count &&
               leftCounts.All(pair => rightCounts.TryGetValue(pair.Key, out var count) && count == pair.Value);
    }

    [GeneratedRegex(@"</?x[1-9][0-9]*/?>", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}

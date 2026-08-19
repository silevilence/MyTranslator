using System.Text.Json;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Terms;

internal static class TermAlignmentMatcher
{
    public static SegmentTermAlignmentResult Match(
        IReadOnlyList<Term> terms,
        string sourceText,
        string targetText,
        string markupTableJson)
    {
        var normalizedSourceText = NormalizeSegmentText(sourceText, markupTableJson);
        var normalizedTargetText = NormalizeSegmentText(targetText, markupTableJson);
        var matchedAny = false;
        var misalignments = new List<TermMisalignmentResponse>();
        foreach (var term in terms)
        {
            var targetTerm = TermText.NormalizeForMatch(term.TargetTerm);
            var comparison = term.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var sourceOccurrences = CountSourceOccurrences(
                normalizedSourceText,
                term.SourceTerm,
                term.CaseSensitive);
            if (sourceOccurrences == 0)
            {
                continue;
            }

            matchedAny = true;
            if (CountOccurrences(normalizedTargetText, targetTerm, comparison) == 0)
            {
                misalignments.Add(new TermMisalignmentResponse(
                    term.Id,
                    term.SourceTerm,
                    term.TargetTerm,
                    term.CaseSensitive,
                    sourceOccurrences,
                    0));
            }
        }

        return new SegmentTermAlignmentResult(matchedAny, misalignments);
    }

    public static int CountSourceOccurrences(
        string sourceText,
        string markupTableJson,
        string sourceTerm,
        bool caseSensitive) => CountSourceOccurrences(
        NormalizeSegmentText(sourceText, markupTableJson),
        sourceTerm,
        caseSensitive);

    private static int CountSourceOccurrences(
        string normalizedSourceText,
        string sourceTerm,
        bool caseSensitive) => CountOccurrences(
        normalizedSourceText,
        TermText.NormalizeForMatch(sourceTerm),
        caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSegmentText(string text, string markupTableJson)
    {
        using var markup = JsonDocument.Parse(markupTableJson);
        foreach (var item in markup.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetInt32();
            if (item.GetProperty("kind").GetString() == "paired")
            {
                text = text.Replace($"<x{id}>", " ", StringComparison.Ordinal)
                    .Replace($"</x{id}>", " ", StringComparison.Ordinal);
            }
            else
            {
                text = text.Replace($"<x{id}/>", " ", StringComparison.Ordinal);
            }
        }

        return TermText.NormalizeForMatch(text);
    }

    private static int CountOccurrences(string text, string value, StringComparison comparison)
    {
        var count = 0;
        var startIndex = 0;
        while (startIndex <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, startIndex, comparison);
            if (index < 0)
            {
                break;
            }

            count++;
            startIndex = index + value.Length;
        }

        return count;
    }
}

internal sealed record SegmentTermAlignmentResult(
    bool MatchedAny,
    IReadOnlyList<TermMisalignmentResponse> Misalignments);

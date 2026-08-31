using System.Text;

namespace MyTranslator.Api.Text;

internal static class TextSimilarity
{
    public static string NormalizeForMatch(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC);
        var result = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(rune.ToString());
        }

        return result.ToString();
    }

    public static double LevenshteinScore(string left, string right)
    {
        var leftRunes = left.EnumerateRunes().ToArray();
        var rightRunes = right.EnumerateRunes().ToArray();
        var maximumLength = Math.Max(leftRunes.Length, rightRunes.Length);
        return maximumLength == 0
            ? 1.0
            : 1.0 - (double)LevenshteinDistance(leftRunes, rightRunes) / maximumLength;
    }

    private static int LevenshteinDistance(Rune[] left, Rune[] right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

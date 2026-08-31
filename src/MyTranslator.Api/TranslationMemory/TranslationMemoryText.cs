using MyTranslator.Api.Text;

namespace MyTranslator.Api.TranslationMemory;

internal static class TranslationMemoryText
{
    public static string NormalizeForSimilarity(string value, string markupTableJson = "[]")
    {
        var text = TranslationMemoryMarkup.RemoveRegisteredReferences(value, markupTableJson);
        return TextSimilarity.NormalizeForMatch(text).ToUpperInvariant();
    }

    public static double Similarity(
        string left,
        string right,
        string leftMarkupTableJson = "[]",
        string rightMarkupTableJson = "[]")
    {
        var leftNormalized = NormalizeForSimilarity(left, leftMarkupTableJson);
        var rightNormalized = NormalizeForSimilarity(right, rightMarkupTableJson);
        var score = TextSimilarity.LevenshteinScore(leftNormalized, rightNormalized);
        return Math.Round(Math.Clamp(score, 0.0, 1.0), 4, MidpointRounding.AwayFromZero);
    }
}

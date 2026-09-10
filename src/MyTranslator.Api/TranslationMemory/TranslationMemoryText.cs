using MyTranslator.Api.Text;

namespace MyTranslator.Api.TranslationMemory;

internal static class TranslationMemoryText
{
    public static string NormalizeForSimilarity(string value, string markupTableJson = "[]")
    {
        var text = TranslationMemoryMarkup.RemoveRegisteredReferences(value, markupTableJson);
        return TextSimilarity.NormalizeForMatch(text).ToUpperInvariant();
    }

    /// <summary>
    /// §7.1 相似度。给出 <paramref name="minimumScore"/>（&gt; <c>0</c>）时先做廉价预筛：Levenshtein 距离不小于
    /// 两侧长度差，故相似度不高于「较短长度 / 较长长度」；该上界低于门槛时必然不达标，直接返回 <c>0</c>，
    /// 跳过昂贵的距离计算。低于门槛的分数不会进入响应（结果按门槛过滤），与全量计算的结论等价。
    /// </summary>
    public static double Similarity(
        string left,
        string right,
        string leftMarkupTableJson = "[]",
        string rightMarkupTableJson = "[]",
        double minimumScore = 0)
    {
        var leftNormalized = NormalizeForSimilarity(left, leftMarkupTableJson);
        var rightNormalized = NormalizeForSimilarity(right, rightMarkupTableJson);
        if (minimumScore > 0 && CannotReach(leftNormalized, rightNormalized, minimumScore))
        {
            return 0;
        }

        var score = TextSimilarity.LevenshteinScore(leftNormalized, rightNormalized);
        return Math.Round(Math.Clamp(score, 0.0, 1.0), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>相似度上界 = 较短长度 / 较长长度；低于门槛即不可能达标（两侧均为空时相似度为 1）。</summary>
    private static bool CannotReach(string leftNormalized, string rightNormalized, double minimumScore)
    {
        var leftLength = leftNormalized.EnumerateRunes().Count();
        var rightLength = rightNormalized.EnumerateRunes().Count();
        var maximum = Math.Max(leftLength, rightLength);
        return maximum != 0 && (double)Math.Min(leftLength, rightLength) / maximum < minimumScore;
    }
}

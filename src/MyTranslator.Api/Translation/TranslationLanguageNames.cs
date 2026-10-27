using System.Globalization;

namespace MyTranslator.Api.Translation;

internal static class TranslationLanguageNames
{
    // 仅使用运行时已知目录，避免 CultureInfo 为未知标签生成原标签形式的名称。
    private static readonly IReadOnlyDictionary<string, string> Names = CultureInfo
        .GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
        .Where(culture => culture.Name.Length > 0)
        .GroupBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().EnglishName, StringComparer.OrdinalIgnoreCase);

    public static string? Find(string language) => language.ToLowerInvariant() switch
    {
        "zh-cn" or "zh-hans" => "Simplified Chinese",
        "zh-tw" or "zh-hant" => "Traditional Chinese",
        _ => Names.GetValueOrDefault(language)
    };
}

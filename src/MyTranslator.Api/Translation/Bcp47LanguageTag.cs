using System.Text.RegularExpressions;

namespace MyTranslator.Api.Translation;

internal static partial class Bcp47LanguageTag
{
    private static readonly HashSet<string> GrandfatheredTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "art-lojban", "cel-gaulish", "en-GB-oed", "i-ami", "i-bnn", "i-default",
        "i-enochian", "i-hak", "i-klingon", "i-lux", "i-mingo", "i-navajo",
        "i-pwn", "i-tao", "i-tay", "i-tsu", "no-bok", "no-nyn", "sgn-BE-FR",
        "sgn-BE-NL", "sgn-CH-DE", "zh-guoyu", "zh-hakka", "zh-min", "zh-min-nan",
        "zh-xiang"
    };

    public static bool TryNormalize(string value, out string normalized)
    {
        if (!LanguageTagPattern().IsMatch(value) && !GrandfatheredTags.Contains(value))
        {
            normalized = string.Empty;
            return false;
        }

        var parts = value.Split('-');
        if (parts[0].Equals("x", StringComparison.OrdinalIgnoreCase) ||
            GrandfatheredTags.Contains(value))
        {
            normalized = string.Join('-', parts.Select(part => part.ToLowerInvariant()));
            return true;
        }

        parts[0] = parts[0].ToLowerInvariant();
        var index = 1;
        if (parts[0].Length is 2 or 3)
        {
            for (var count = 0; count < 3 && index < parts.Length && IsAlpha(parts[index], 3); count++)
            {
                parts[index] = parts[index].ToLowerInvariant();
                index++;
            }
        }

        if (index < parts.Length && IsAlpha(parts[index], 4))
        {
            parts[index] = char.ToUpperInvariant(parts[index][0]) + parts[index][1..].ToLowerInvariant();
            index++;
        }

        if (index < parts.Length &&
            (IsAlpha(parts[index], 2) || parts[index].Length == 3 && parts[index].All(char.IsAsciiDigit)))
        {
            parts[index] = parts[index].ToUpperInvariant();
            index++;
        }

        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < parts.Length && IsVariant(parts[index]))
        {
            if (!variants.Add(parts[index]))
            {
                normalized = string.Empty;
                return false;
            }

            parts[index] = parts[index].ToLowerInvariant();
            index++;
        }

        var extensionSingletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < parts.Length && parts[index].Length == 1 &&
               !parts[index].Equals("x", StringComparison.OrdinalIgnoreCase))
        {
            if (!extensionSingletons.Add(parts[index]))
            {
                normalized = string.Empty;
                return false;
            }

            parts[index] = parts[index].ToLowerInvariant();
            index++;
            while (index < parts.Length && parts[index].Length > 1)
            {
                parts[index] = parts[index].ToLowerInvariant();
                index++;
            }
        }

        for (; index < parts.Length; index++)
        {
            parts[index] = parts[index].ToLowerInvariant();
        }

        normalized = string.Join('-', parts);
        return true;
    }

    private static bool IsAlpha(string value, int length) =>
        value.Length == length && value.All(char.IsAsciiLetter);

    private static bool IsVariant(string value) =>
        value.Length is >= 5 and <= 8 ||
        value.Length == 4 && char.IsAsciiDigit(value[0]);

    [GeneratedRegex(
        "^(?:(?:[A-Za-z]{2,3}(?:-[A-Za-z]{3}){0,3}|[A-Za-z]{4}|[A-Za-z]{5,8})" +
        "(?:-[A-Za-z]{4})?(?:-(?:[A-Za-z]{2}|[0-9]{3}))?" +
        "(?:-(?:[A-Za-z0-9]{5,8}|[0-9][A-Za-z0-9]{3}))*" +
        "(?:-[0-9A-WY-Za-wy-z](?:-[A-Za-z0-9]{2,8})+)*" +
        "(?:-x(?:-[A-Za-z0-9]{1,8})+)?|x(?:-[A-Za-z0-9]{1,8})+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagPattern();
}

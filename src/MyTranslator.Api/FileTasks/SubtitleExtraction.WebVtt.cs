using System.Globalization;
using System.Text.RegularExpressions;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.FileTasks;

internal static partial class SubtitleExtraction
{
    private static bool IsVttProtectedBlock(List<Line> block, int cue, int line)
    {
        var first = block[0].Text;
        if (first == "NOTE" || first.StartsWith("NOTE ", StringComparison.Ordinal) || first.StartsWith("NOTE\t", StringComparison.Ordinal))
        {
            if (block.Any(item => item.Text.Contains("-->", StringComparison.Ordinal))) throw ParseError("vtt", cue, line);
            return true;
        }
        if (first is not ("STYLE" or "REGION")) return false;
        if (cue > 0 || block.Count == 1 || block.Any(item => item.Text.Contains("-->", StringComparison.Ordinal)))
            throw ParseError("vtt", cue, line);
        if (first == "REGION")
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setting in block.Skip(1).SelectMany(item => item.Text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))
            {
                var pair = setting.Split(':', 2);
                if (pair.Length != 2 || !seen.Add(pair[0])) throw ParseError("vtt", cue, line);
                var valid = pair[0] switch
                {
                    "id" => pair[1].Length > 0,
                    "width" => Percentage(pair[1]),
                    "lines" => Regex.IsMatch(pair[1], @"^[0-9]+$", RegexOptions.CultureInvariant),
                    "regionanchor" or "viewportanchor" => pair[1].Split(',') is var coordinates &&
                        coordinates.Length == 2 && coordinates.All(Percentage),
                    "scroll" => pair[1] == "up",
                    _ => false
                };
                if (!valid) throw ParseError("vtt", cue, line);
            }
            if (!seen.Contains("id")) throw ParseError("vtt", cue, line);
        }
        return true;
    }

    private static bool ValidSettings(string settings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in settings.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = setting.Split(':', 2);
            if (pair.Length != 2 || !seen.Add(pair[0])) return false;
            var parts = pair[1].Split(',');
            var valid = pair[0] switch
            {
                "vertical" => pair[1] is "rl" or "lr",
                "line" => parts.Length <= 2 && (parts[0] == "auto" || Percentage(parts[0]) ||
                    Regex.IsMatch(parts[0], @"^-?[0-9]+$", RegexOptions.CultureInvariant)) &&
                    (parts.Length == 1 || parts[1] is "start" or "center" or "end"),
                "position" => parts.Length <= 2 && Percentage(parts[0]) &&
                    (parts.Length == 1 || parts[1] is "line-left" or "center" or "line-right" or "auto"),
                "size" => Percentage(pair[1]),
                "align" => pair[1] is "start" or "center" or "end" or "left" or "right",
                "region" => pair[1].Length > 0 && !pair[1].Contains("-->", StringComparison.Ordinal),
                _ => false
            };
            if (!valid) return false;
        }
        return true;
    }

    private static bool Percentage(string text) => Regex.IsMatch(text, @"^[0-9]+(?:\.[0-9]+)?%$", RegexOptions.CultureInvariant) &&
        decimal.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) && number <= 100;

    private static bool ValidVttTag(string name, string attributes, string? parent) => name switch
    {
        "v" => !string.IsNullOrWhiteSpace(attributes),
        "lang" => Bcp47LanguageTag.TryNormalize(attributes.Trim(), out _),
        "rt" => parent == "ruby" && attributes.Length == 0,
        _ => attributes.Length == 0
    };

    [GeneratedRegex(@"^WEBVTT(?:[ \t][^\r\n]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VttHeader();
    [GeneratedRegex(@"^(?<start>(?:[0-9]{2,6}:)?[0-9]{2}:[0-9]{2}\.[0-9]{3})[ \t]+-->[ \t]+(?<end>(?:[0-9]{2,6}:)?[0-9]{2}:[0-9]{2}\.[0-9]{3})(?<settings>(?:[ \t]+[^\r\n]*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex VttTiming();
    [GeneratedRegex(@"^<(?:[0-9]{2,6}:)?[0-9]{2}:[0-9]{2}\.[0-9]{3}>$", RegexOptions.CultureInvariant)]
    private static partial Regex VttTimestamp();
    [GeneratedRegex(@"^<(?<closing>/)?(?<name>b|i|u|c|ruby|rt|v|lang)(?<classes>(?:\.[^\s.<>/&]+)*)(?<attributes>(?:[ \t]+[^<>&\r\n]+)?)>$", RegexOptions.CultureInvariant)]
    private static partial Regex VttTag();
}

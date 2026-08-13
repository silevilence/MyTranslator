using System.Security.Cryptography;
using System.Text;

namespace MyTranslator.Api.FileTasks;

internal static class TextExtraction
{
    public static TextExtractionResult Extract(byte[] bytes, string? requestedMode)
    {
        var (text, encodingName) = Decode(bytes);
        var lines = ReadLines(text);
        var recommended = lines.Any(line => line.IsBlank)
            ? "paragraph"
            : "line";
        var reason = recommended == "paragraph"
            ? "blank_line_blocks_present"
            : "few_or_no_blank_lines";
        var effective = requestedMode ?? recommended;
        if (effective is not ("paragraph" or "line"))
        {
            throw new InvalidFileTaskRequestException(
                "invalid_segmentation_mode",
                "Segmentation mode must be paragraph or line.");
        }

        var spans = effective == "paragraph"
            ? ParagraphSpans(lines)
            : LineSpans(lines);
        var units = BuildUnits(text, spans);
        return new TextExtractionResult(text, encodingName, requestedMode, recommended, effective, reason, units);
    }

    internal static (string Text, string EncodingName) Decode(byte[] bytes, string? declaredEncoding = null)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return (Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length)), "utf-8");
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return (
                Encoding.Unicode.GetString(bytes.AsSpan(Encoding.Unicode.Preamble.Length)),
                "utf-16");
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return (
                Encoding.BigEndianUnicode.GetString(bytes.AsSpan(Encoding.BigEndianUnicode.Preamble.Length)),
                "utf-16be");
        }

        if (!string.IsNullOrWhiteSpace(declaredEncoding))
        {
            try
            {
                var encoding = Encoding.GetEncoding(
                    declaredEncoding,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                return (encoding.GetString(bytes), encoding.WebName.ToLowerInvariant());
            }
            catch (Exception exception) when (exception is ArgumentException or DecoderFallbackException)
            {
                throw new InvalidFileTaskRequestException(
                    "import_parse_failed",
                    "The declared text encoding is invalid or does not match the content.",
                    exception);
            }
        }

        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidFileTaskRequestException(
                "import_parse_failed",
                "The text file is not valid UTF-8.",
                exception);
        }
    }

    private static List<TextLine> ReadLines(string text)
    {
        var lines = new List<TextLine>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            var newlineLength = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n'
                ? 2
                : 1;
            lines.Add(new TextLine(start, index - start, newlineLength, IsBlank(text.AsSpan(start, index - start))));
            index += newlineLength - 1;
            start = index + 1;
        }

        if (start < text.Length || text.Length == 0)
        {
            lines.Add(new TextLine(start, text.Length - start, 0, IsBlank(text.AsSpan(start))));
        }

        return lines;
    }

    private static bool IsBlank(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    private static List<TextSpan> ParagraphSpans(IReadOnlyList<TextLine> lines)
    {
        var spans = new List<TextSpan>();
        var first = -1;
        var last = -1;
        foreach (var line in lines)
        {
            if (line.IsBlank)
            {
                if (first >= 0)
                {
                    spans.Add(new TextSpan(first, last - first));
                    first = -1;
                }

                continue;
            }

            first = first < 0 ? line.Start : first;
            last = line.Start + line.ContentLength;
        }

        if (first >= 0)
        {
            spans.Add(new TextSpan(first, last - first));
        }

        return spans;
    }

    private static List<TextSpan> LineSpans(IEnumerable<TextLine> lines) => lines
        .Where(line => !line.IsBlank)
        .Select(line => new TextSpan(line.Start, line.ContentLength))
        .ToList();

    private static List<ExtractedUnit> BuildUnits(string text, IReadOnlyList<TextSpan> segmentSpans)
    {
        var units = new List<ExtractedUnit>();
        var position = 0;
        foreach (var span in segmentSpans)
        {
            if (position < span.Start)
            {
                units.Add(ExtractedUnit.Protected(text[position..span.Start]));
            }

            units.Add(ExtractedUnit.Segment(text.Substring(span.Start, span.Length)));
            position = span.Start + span.Length;
        }

        if (position < text.Length)
        {
            units.Add(ExtractedUnit.Protected(text[position..]));
        }

        return units;
    }

    internal static string Hash(string value, string encodingName) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.GetEncoding(encodingName).GetBytes(value)))}";

    private sealed record TextLine(int Start, int ContentLength, int NewlineLength, bool IsBlank);
    private sealed record TextSpan(int Start, int Length);
}

internal sealed record TextExtractionResult(
    string OriginalText,
    string EncodingName,
    string? RequestedMode,
    string? RecommendedMode,
    string? EffectiveMode,
    string? Reason,
    IReadOnlyList<ExtractedUnit> Units);

internal sealed record ExtractedUnit(bool IsSegment, string Text, string MarkupTableJson)
{
    public static ExtractedUnit Segment(string text, string markupTableJson = "[]") =>
        new(true, text, markupTableJson);

    public static ExtractedUnit Protected(string text) => new(false, text, "[]");
}

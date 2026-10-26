using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MyTranslator.Api.FileTasks;

/// <summary>仅替换字幕正文范围；所有条目边界和时间轴保留在重建模板中。</summary>
internal static partial class SubtitleExtraction
{
    public static TextExtractionResult Extract(byte[] bytes, string fileType)
    {
        var (text, encoding) = Decode(bytes, fileType);
        if (text.Any(c => (char.IsControl(c) && c is not ('\t' or '\r' or '\n')) || c is '\u2028' or '\u2029' or '\uFEFF'))
            throw ParseError(fileType, 0, 1);
        var lines = Lines(text);
        var units = new List<ExtractedUnit>();
        var position = 0;
        var cue = 0;
        var firstLine = 0;
        var previousStart = -1L;
        if (fileType == "vtt")
        {
            if (lines.Count == 0 || !VttHeader().IsMatch(lines[0].Text) || lines[0].Text.Contains("-->", StringComparison.Ordinal) ||
                (lines.Count > 1 && lines[1].Text.Length != 0)) throw ParseError(fileType, 0, 1);
            firstLine = 1;
        }
        for (var index = firstLine; index < lines.Count;)
        {
            if (string.IsNullOrWhiteSpace(lines[index].Text)) { index++; continue; }
            var first = index;
            while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index].Text)) index++;
            var block = lines.GetRange(first, index - first);
            if (fileType == "vtt" && IsVttProtectedBlock(block, cue, first + 1)) continue;
            cue++;
            var timingIndex = fileType == "vtt" && block[0].Text.Contains("-->", StringComparison.Ordinal) ? 0 : 1;
            if (block.Count <= timingIndex || (fileType == "srt" && !Regex.IsMatch(block[0].Text, @"^[0-9]+$", RegexOptions.CultureInvariant)))
                throw ParseError(fileType, cue, first + 1);
            var timing = (fileType == "vtt" ? VttTiming() : SrtTiming()).Match(block[timingIndex].Text);
            if (!timing.Success || !TryTime(timing.Groups["start"].Value, out var start) ||
                !TryTime(timing.Groups["end"].Value, out var end) || end <= start)
                throw ParseError(fileType, cue, first + 2);
            if (fileType == "vtt")
            {
                if (start < previousStart || !ValidSettings(timing.Groups["settings"].Value))
                    throw ParseError(fileType, cue, first + timingIndex + 1);
                previousStart = start;
            }
            if (block.Count == timingIndex + 1) continue;
            var bodyStart = block[timingIndex + 1].Start;
            var bodyEnd = block[^1].Start + block[^1].Text.Length;
            var body = text[bodyStart..bodyEnd];
            var extracted = ExtractBody(body, fileType, cue, first + timingIndex + 2, start, end);
            if (!MarkupExtraction.HasTranslatableText(extracted.Text, extracted.MarkupTableJson)) continue;
            if (bodyStart > position) units.Add(ExtractedUnit.Protected(text[position..bodyStart], "subtitleStructure"));
            units.Add(extracted);
            position = bodyEnd;
        }
        if (cue == 0 && fileType != "vtt") throw ParseError(fileType, 0, 1);
        if (position < text.Length) units.Add(ExtractedUnit.Protected(text[position..], "subtitleStructure"));
        return new(text, encoding, null, null, null, null, units);
    }

    internal static ExtractedUnit ExtractBody(string body, string type, int cue, int line, long start, long end)
    {
        if (!IsPlainBodyValid(body, allowMarkup: true)) throw ParseError(type, cue, line);
        var result = new StringBuilder();
        var items = new List<MarkupItem>();
        var stack = new Stack<(string Name, MarkupItem Item)>();
        var allocator = new PlaceholderIdAllocator(string.Empty);
        var previousTimestamp = start;
        for (var index = 0; index < body.Length;)
        {
            var character = body[index];
            if (character == '&')
            {
                var entity = Entity().Match(body, index);
                if (!entity.Success || entity.Index != index || !ValidEntity(entity.Value, type))
                    throw ParseError(type, cue, line);
                var item = MarkupItem.Standalone(allocator.Next(), entity.Value, "字幕实体");
                items.Add(item);
                result.Append($"<x{item.Id}/>");
                index += entity.Length;
            }
            else if (character == '<')
            {
                var close = body.IndexOf('>', index);
                if (close < 0) throw ParseError(type, cue, line);
                var tag = body[index..(close + 1)];
                if (type == "vtt" && VttTimestamp().IsMatch(tag))
                {
                    if (!TryTime(tag[1..^1], out var timestamp) || timestamp <= previousTimestamp || timestamp >= end)
                        throw ParseError(type, cue, line);
                    previousTimestamp = timestamp;
                    var marker = MarkupItem.Standalone(allocator.Next(), tag, "字幕段内时间标记");
                    items.Add(marker);
                    result.Append($"<x{marker.Id}/>");
                    index = close + 1;
                    continue;
                }
                var match = (type == "vtt" ? VttTag() : SrtTag()).Match(tag);
                if (!match.Success) throw ParseError(type, cue, line);
                var name = match.Groups["name"].Value.ToLowerInvariant();
                if (match.Groups["closing"].Success)
                {
                    if (match.Groups["attributes"].Length > 0 || match.Groups["classes"].Length > 0 || !stack.TryPop(out var opened) || opened.Name != name)
                        throw ParseError(type, cue, line);
                    opened.Item.ClosingText = tag;
                    result.Append($"</x{opened.Item.Id}>");
                }
                else
                {
                    var attributes = match.Groups["attributes"].Value;
                    if (type == "srt" && (name == "font" ? !FontColor().IsMatch(attributes) : attributes.Length > 0))
                        throw ParseError(type, cue, line);
                    if (type == "vtt" && !ValidVttTag(name, attributes, stack.Count > 0 ? stack.Peek().Name : null))
                        throw ParseError(type, cue, line);
                    var item = MarkupItem.Paired(allocator.Next(), tag, "字幕样式：" + name);
                    items.Add(item);
                    stack.Push((name, item));
                    result.Append($"<x{item.Id}>");
                }
                index = close + 1;
            }
            else
            {
                result.Append(character);
                index++;
            }
        }
        // WebVTT 允许覆盖整个条目的外层说话人/语言跨度省略闭标签。
        if (type == "vtt" && stack.Count == 1 && stack.Peek().Name is "v" or "lang")
        {
            var opened = stack.Pop();
            opened.Item.ClosingText = string.Empty;
            result.Append($"</x{opened.Item.Id}>");
        }
        if (stack.Count != 0) throw ParseError(type, cue, line);
        return ExtractedUnit.Segment(result.ToString(), MarkupExtraction.Serialize(items));
    }

    internal static bool IsPlainBodyValid(string text, bool allowMarkup = false)
    {
        if (text.Contains("-->", StringComparison.Ordinal) || text.IndexOfAny(['{', '}']) >= 0 ||
            (!allowMarkup && text.IndexOfAny(['<', '&']) >= 0)) return false;
        foreach (var c in text)
            if ((char.IsControl(c) && c is not ('\t' or '\n' or '\r')) || c is '\u2028' or '\u2029' or '\uFEFF') return false;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').All(line => !string.IsNullOrWhiteSpace(line));
    }

    private static bool ValidEntity(string value, string type)
    {
        if (value is "&amp;" or "&lt;" or "&gt;" or "&nbsp;" or "&lrm;" or "&rlm;") return true;
        if (type != "srt") return false;
        if (value is "&quot;" or "&apos;") return true;
        if (!value.StartsWith("&#", StringComparison.Ordinal)) return false;
        var hex = value.StartsWith("&#x", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(value.AsSpan(hex ? 3 : 2, value.Length - (hex ? 4 : 3)),
            hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var scalar) &&
            Rune.IsValid(scalar) && scalar >= 32 && scalar is not (127 or 0x2028 or 0x2029 or 0xFEFF) &&
            (scalar < 128 || scalar > 159);
    }

    private static (string Text, string Encoding) Decode(byte[] bytes, string type)
    {
        try
        {
            Encoding encoding = new UTF8Encoding(false, true);
            var skip = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) ||
                bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) throw new DecoderFallbackException();
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) skip = 3;
            else if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
            {
                if (type != "srt") throw new DecoderFallbackException();
                encoding = new UnicodeEncoding(false, false, true); skip = 2;
            }
            else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
            {
                if (type != "srt") throw new DecoderFallbackException();
                encoding = new UnicodeEncoding(true, false, true); skip = 2;
            }
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            if (text.Contains('\0')) throw new DecoderFallbackException();
            return (text, encoding.WebName);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidFileTaskRequestException("subtitle_encoding_invalid", "Invalid subtitle encoding.", exception);
        }
    }

    private static List<Line> Lines(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            lines.Add(new Line(start, text[start..i]));
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < text.Length) lines.Add(new Line(start, text[start..]));
        return lines;
    }

    private static bool TryTime(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Replace(',', '.').Split(':', '.');
        if (parts.Length is not (3 or 4)) return false;
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        var hours = parts.Length == 4 ? numbers[0] : 0;
        var minutes = numbers[^3]; var seconds = numbers[^2]; var fraction = numbers[^1];
        if (minutes > 59 || seconds > 59 || fraction > 999 || hours > 999999) return false;
        milliseconds = ((hours * 60 + minutes) * 60 + seconds) * 1000 + fraction;
        return true;
    }

    private static InvalidFileTaskRequestException ParseError(string type, int cue, int line) => new(
        "subtitle_parse_failed", "Invalid or unsupported subtitle structure.", errors: new Dictionary<string, object?>
        { ["fileType"] = type, ["cue"] = cue, ["line"] = line });

    private sealed record Line(int Start, string Text);

    [GeneratedRegex(@"^(?<start>[0-9]{2,6}:[0-9]{2}:[0-9]{2},[0-9]{3})[ \t]+-->[ \t]+(?<end>[0-9]{2,6}:[0-9]{2}:[0-9]{2},[0-9]{3})[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SrtTiming();
    [GeneratedRegex(@"^<(?<closing>/)?(?<name>b|i|u|font)(?<attributes>[^<>]*)>$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SrtTag();
    [GeneratedRegex("^[ \\t]+color[ \\t]*=[ \\t]*(?:\"(?:#[0-9a-f]{6}|[a-z]+)\"|'(?:#[0-9a-f]{6}|[a-z]+)'|#[0-9a-f]{6}|[a-z]+)[ \\t]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FontColor();
    [GeneratedRegex(@"&(?:[a-z]+|#[0-9]+|#x[0-9a-f]+);", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Entity();
}

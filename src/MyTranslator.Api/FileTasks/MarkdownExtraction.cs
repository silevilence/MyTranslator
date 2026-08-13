using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace MyTranslator.Api.FileTasks;

internal static partial class MarkdownExtraction
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static TextExtractionResult Extract(byte[] bytes)
    {
        var (text, encodingName) = TextExtraction.Decode(bytes);
        var document = Markdown.Parse(text, Pipeline);
        var units = new List<ExtractedUnit>();
        var position = 0;

        foreach (var block in document)
        {
            var start = Math.Max(0, block.Span.Start);
            var end = Math.Min(text.Length, block.Span.End + 1);
            if (start > position)
            {
                units.Add(ExtractedUnit.Protected(text[position..start]));
            }

            var raw = text[start..end];
            if (IsProtected(block) || string.IsNullOrWhiteSpace(raw))
            {
                units.Add(ExtractedUnit.Protected(raw, ProtectedType(block, raw)));
            }
            else
            {
                if (block is Table)
                {
                    AddTableUnits(raw, units);
                }
                else if (BlockPrefixRegex().IsMatch(raw))
                {
                    AddLineBlockUnits(raw, units);
                }
                else
                {
                    var segment = ExtractMarkup(raw);
                    if (MarkupExtraction.HasTranslatableText(segment.SourceText, segment.MarkupTableJson))
                    {
                        units.Add(ExtractedUnit.Segment(segment.SourceText, segment.MarkupTableJson));
                    }
                    else
                    {
                        units.Add(ExtractedUnit.Protected(raw));
                    }
                }
            }

            position = end;
        }

        if (position < text.Length)
        {
            units.Add(ExtractedUnit.Protected(text[position..]));
        }

        return new TextExtractionResult(text, encodingName, null, null, null, null, units);
    }

    private static bool IsProtected(Block block) =>
        block is CodeBlock or HtmlBlock or ThematicBreakBlock;

    private static string ProtectedType(Block block, string raw) => block switch
    {
        CodeBlock => "markdownCodeBlock",
        HtmlBlock => "markdownHtmlBlock",
        ThematicBreakBlock => "markdownThematicBreak",
        _ when string.IsNullOrWhiteSpace(raw) => "textWhitespace",
        _ => "markdownStructure"
    };

    private static void AddLineBlockUnits(string raw, ICollection<ExtractedUnit> units)
    {
        var position = 0;
        while (position < raw.Length)
        {
            var newlineIndex = raw.IndexOfAny(['\r', '\n'], position);
            var contentEnd = newlineIndex < 0 ? raw.Length : newlineIndex;
            var content = raw[position..contentEnd];
            if (string.IsNullOrWhiteSpace(content))
            {
                units.Add(ExtractedUnit.Protected(content));
            }
            else
            {
                var segment = ExtractMarkup(content);
                units.Add(MarkupExtraction.HasTranslatableText(segment.SourceText, segment.MarkupTableJson)
                    ? ExtractedUnit.Segment(segment.SourceText, segment.MarkupTableJson)
                    : ExtractedUnit.Protected(content));
            }

            if (newlineIndex < 0)
            {
                break;
            }

            var newlineLength = raw[newlineIndex] == '\r' &&
                                newlineIndex + 1 < raw.Length &&
                                raw[newlineIndex + 1] == '\n'
                ? 2
                : 1;
            units.Add(ExtractedUnit.Protected(raw.Substring(newlineIndex, newlineLength)));
            position = newlineIndex + newlineLength;
        }
    }

    private static void AddTableUnits(string raw, ICollection<ExtractedUnit> units)
    {
        var position = 0;
        while (position < raw.Length)
        {
            var newlineIndex = raw.IndexOfAny(['\r', '\n'], position);
            var contentEnd = newlineIndex < 0 ? raw.Length : newlineIndex;
            var content = raw[position..contentEnd];
            if (TableSeparatorRegex().IsMatch(content) || string.IsNullOrWhiteSpace(content))
            {
                units.Add(ExtractedUnit.Protected(content));
            }
            else
            {
                var segment = ExtractMarkup(content, protectTablePipes: true);
                units.Add(MarkupExtraction.HasTranslatableText(segment.SourceText, segment.MarkupTableJson)
                    ? ExtractedUnit.Segment(segment.SourceText, segment.MarkupTableJson)
                    : ExtractedUnit.Protected(content));
            }

            if (newlineIndex < 0)
            {
                break;
            }

            var newlineLength = raw[newlineIndex] == '\r' && newlineIndex + 1 < raw.Length && raw[newlineIndex + 1] == '\n'
                ? 2
                : 1;
            units.Add(ExtractedUnit.Protected(raw.Substring(newlineIndex, newlineLength)));
            position = newlineIndex + newlineLength;
        }
    }

    private static MarkupExtractionResult ExtractMarkup(string raw, bool protectTablePipes = false)
    {
        var items = new List<MarkupItem>();
        var allocator = new PlaceholderIdAllocator(raw);
        var builder = new StringBuilder();
        var position = 0;
        var blockPrefix = BlockPrefixRegex().Match(raw);
        if (blockPrefix.Success)
        {
            var id = allocator.Next();
            items.Add(MarkupItem.Standalone(id, blockPrefix.Value, "Markdown 块级标记"));
            builder.Append($"<x{id}/>");
            position = blockPrefix.Length;
        }

        foreach (Match match in InlineMarkupRegex().Matches(raw, position))
        {
            builder.Append(raw, position, match.Index - position);
            if (match.Groups["pipe"].Success && !protectTablePipes)
            {
                builder.Append(match.Value);
                position = match.Index + match.Length;
                continue;
            }

            var id = allocator.Next();
            if (match.Groups["bold"].Success)
            {
                items.Add(MarkupItem.Paired(id, "**", "**", "Markdown 加粗强调"));
                builder.Append($"<x{id}>{match.Groups["bold"].Value}</x{id}>");
            }
            else if (match.Groups["emphasis"].Success)
            {
                var delimiter = match.Groups["emphasisDelimiter"].Value;
                items.Add(MarkupItem.Paired(id, delimiter, delimiter, "Markdown 强调"));
                builder.Append($"<x{id}>{match.Groups["emphasis"].Value}</x{id}>");
            }
            else if (match.Groups["image"].Success)
            {
                items.Add(MarkupItem.Standalone(id, match.Value, "Markdown 图片"));
                builder.Append($"<x{id}/>");
            }
            else if (match.Groups["link"].Success)
            {
                items.Add(MarkupItem.Paired(
                    id,
                    "[",
                    $"]({match.Groups["target"].Value})",
                    "Markdown 链接"));
                builder.Append($"<x{id}>{match.Groups["link"].Value}</x{id}>");
            }
            else if (match.Groups["code"].Success)
            {
                items.Add(MarkupItem.Standalone(id, match.Value, "Markdown 行内代码"));
                builder.Append($"<x{id}/>");
            }
            else
            {
                items.Add(MarkupItem.Standalone(id, match.Value, "Markdown 表格分隔符"));
                builder.Append($"<x{id}/>");
            }

            position = match.Index + match.Length;
        }

        builder.Append(raw, position, raw.Length - position);
        return new MarkupExtractionResult(
            builder.ToString(),
            MarkupExtraction.Serialize(items));
    }

    [GeneratedRegex(@"^(?:(?:#{1,6})[ \t]+|>[ \t]?|(?:[-+*]|\d+[.)])[ \t]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BlockPrefixRegex();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|(?<emphasisDelimiter>\*|_)(?<emphasis>[^*_\r\n]+)\k<emphasisDelimiter>|(?<image>!\[[^\]\r\n]*\]\([^)\r\n]+\))|\[(?<link>[^\]\r\n]+)\]\((?<target>[^)\r\n]+)\)|(?<code>`[^`\r\n]+`)|(?<pipe>\|)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineMarkupRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparatorRegex();

    private sealed record MarkupExtractionResult(string SourceText, string MarkupTableJson);
}

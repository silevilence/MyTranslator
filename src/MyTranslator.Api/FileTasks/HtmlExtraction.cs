using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Parser.Tokens;
using AngleSharp.Text;

namespace MyTranslator.Api.FileTasks;

internal static partial class HtmlExtraction
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "caption", "dd", "div", "dt",
        "figcaption", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header",
        "li", "main", "nav", "p", "section", "td", "th"
    };

    private static readonly HashSet<string> ExcludedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "iframe", "svg", "pre", "code"
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta",
        "param", "source", "track", "wbr"
    };

    public static TextExtractionResult Extract(
        byte[] bytes,
        string? selector = null,
        bool allowSelectorNoMatch = false,
        string? declaredEncoding = null)
    {
        var (text, encodingName) = TextExtraction.Decode(bytes, declaredEncoding);
        var parsed = Parse(text);
        var body = parsed.Document.Body;
        if (body is null)
        {
            return new TextExtractionResult(
                text,
                encodingName,
                null,
                null,
                null,
                null,
                [ExtractedUnit.Protected(text)]);
        }

        IReadOnlyList<IElement> scopes;
        if (selector is null)
        {
            scopes = [body];
        }
        else
        {
            try
            {
                scopes = parsed.Document.QuerySelectorAll(selector)
                    .Where(element => ReferenceEquals(element, body) || body.Contains(element))
                    .ToList();
            }
            catch (Exception exception)
            {
                throw new InvalidFileTaskRequestException(
                    "invalid_selector",
                    "The CSS selector is invalid or unsupported.",
                    exception);
            }

            if (scopes.Count == 0)
            {
                if (allowSelectorNoMatch)
                {
                    return new TextExtractionResult(
                        text,
                        encodingName,
                        null,
                        null,
                        null,
                        null,
                        [ExtractedUnit.Protected(text)]);
                }

                throw new InvalidFileTaskRequestException(
                    "selector_no_match",
                    "The CSS selector did not match any elements inside the document body.");
            }
        }

        var orderedCandidates = scopes
            .SelectMany(FindLeafCandidates)
            .Distinct()
            .Select(element => TryGetRange(element, parsed.ElementRanges))
            .Where(range => range is not null)
            .Select(range => range!)
            .OrderBy(range => range.Start);
        var candidates = NonOverlapping(orderedCandidates).ToList();

        if (selector is not null && candidates.Count == 0)
        {
            throw new InvalidFileTaskRequestException(
                "selector_no_match",
                "The CSS selector did not match a translatable element inside the document body.");
        }

        var units = new List<ExtractedUnit>();
        var position = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Start > position)
            {
                units.Add(ExtractedUnit.Protected(text[position..candidate.Start]));
            }

            var raw = text[candidate.Start..candidate.End];
            var segment = ExtractMarkup(raw);
            units.Add(MarkupExtraction.HasTranslatableText(segment.SourceText, segment.MarkupTableJson)
                ? ExtractedUnit.Segment(segment.SourceText, segment.MarkupTableJson)
                : ExtractedUnit.Protected(raw));
            position = candidate.End;
        }

        if (position < text.Length)
        {
            units.Add(ExtractedUnit.Protected(text[position..]));
        }

        return new TextExtractionResult(text, encodingName, null, null, null, null, units);
    }

    private static ParsedHtml Parse(string text)
    {
        var tokens = new List<HtmlSourceToken>();
        var options = new HtmlParserOptions
        {
            IsKeepingSourceReferences = true,
            OnToken = (token, range) =>
            {
                if (range.Start.Index >= 0 && range.Start.Index < text.Length && token.Type != HtmlTokenType.EndOfFile)
                {
                    tokens.Add(new HtmlSourceToken(
                        token.Type,
                        token.Name,
                        range.Start.Index,
                        Math.Min(range.End.Index, text.Length),
                        token is HtmlTagToken tag && tag.IsSelfClosing));
                }
            }
        };

        try
        {
            var document = new HtmlParser(options).ParseDocument(text);
            return new ParsedHtml(document, tokens, BuildElementRanges(tokens));
        }
        catch (Exception exception)
        {
            throw new InvalidFileTaskRequestException(
                "import_parse_failed",
                "The HTML document could not be parsed.",
                exception);
        }
    }

    private static IReadOnlyList<IElement> FindLeafCandidates(IElement scope)
    {
        if (IsExcluded(scope) || scope.Ancestors().OfType<IElement>().Any(IsExcluded))
        {
            return [];
        }

        var leafBlocks = scope.QuerySelectorAll(string.Join(',', BlockTags))
            .Where(element => !IsExcluded(element) && !element.Ancestors().OfType<IElement>().Any(IsExcluded))
            .Where(element => !element.QuerySelectorAll(string.Join(',', BlockTags))
                .Any(descendant => !IsExcluded(descendant) &&
                    !descendant.Ancestors().OfType<IElement>().Any(IsExcluded)))
            .ToList();
        if (leafBlocks.Count > 0)
        {
            return leafBlocks;
        }

        return [scope];
    }

    private static bool IsExcluded(IElement element) => ExcludedTags.Contains(element.LocalName);

    private static HtmlRange? TryGetRange(
        IElement element,
        IReadOnlyDictionary<int, HtmlRange> ranges)
    {
        var start = element.SourceReference?.Position.Index;
        return start is not null && ranges.TryGetValue(start.Value, out var range) ? range : null;
    }

    private static IEnumerable<HtmlRange> NonOverlapping(IEnumerable<HtmlRange> ranges)
    {
        var end = -1;
        foreach (var range in ranges)
        {
            if (range.Start >= end)
            {
                yield return range;
                end = range.End;
            }
        }
    }

    private static Dictionary<int, HtmlRange> BuildElementRanges(IReadOnlyList<HtmlSourceToken> tokens)
    {
        var ranges = new Dictionary<int, HtmlRange>();
        var stack = new List<HtmlSourceToken>();
        foreach (var token in tokens)
        {
            if (token.Type == HtmlTokenType.StartTag)
            {
                if (token.IsSelfClosing || VoidTags.Contains(token.Name))
                {
                    ranges[token.Start] = new HtmlRange(token.Start, token.End);
                }
                else
                {
                    stack.Add(token);
                }
            }
            else if (token.Type == HtmlTokenType.EndTag)
            {
                var index = stack.FindLastIndex(candidate =>
                    candidate.Name.Equals(token.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var opening = stack[index];
                    stack.RemoveRange(index, stack.Count - index);
                    ranges[opening.Start] = new HtmlRange(opening.Start, token.End);
                }
            }
        }

        return ranges;
    }

    private static MarkupExtractionResult ExtractMarkup(string raw)
    {
        var parsed = Parse(raw);
        var allocator = new PlaceholderIdAllocator(raw);
        var items = new List<MarkupItem>();
        var openings = new Stack<(string Name, int Id)>();
        var builder = new StringBuilder();
        var position = 0;

        for (var index = 0; index < parsed.Tokens.Count; index++)
        {
            var token = parsed.Tokens[index];
            if (token.Start < position)
            {
                continue;
            }

            builder.Append(raw, position, token.Start - position);
            var tokenRaw = raw[token.Start..token.End];
            if (token.Type == HtmlTokenType.StartTag && ExcludedTags.Contains(token.Name) &&
                parsed.ElementRanges.TryGetValue(token.Start, out var excludedRange))
            {
                var id = allocator.Next();
                items.Add(MarkupItem.Standalone(id, raw[token.Start..excludedRange.End], $"HTML {token.Name} 受保护元素"));
                builder.Append($"<x{id}/>");
                position = excludedRange.End;
                continue;
            }

            if (token.Type == HtmlTokenType.StartTag)
            {
                var id = allocator.Next();
                if (token.IsSelfClosing || VoidTags.Contains(token.Name))
                {
                    items.Add(MarkupItem.Standalone(id, tokenRaw, $"HTML {token.Name} 标签"));
                    builder.Append($"<x{id}/>");
                }
                else
                {
                    items.Add(MarkupItem.Paired(id, tokenRaw, $"HTML {token.Name} 标签"));
                    openings.Push((token.Name, id));
                    builder.Append($"<x{id}>");
                }
            }
            else if (token.Type == HtmlTokenType.EndTag)
            {
                var opening = PopOpening(openings, token.Name);
                if (opening is null)
                {
                    var id = allocator.Next();
                    items.Add(MarkupItem.Standalone(id, tokenRaw, $"HTML {token.Name} 结束标签"));
                    builder.Append($"<x{id}/>");
                }
                else
                {
                    items.Single(item => item.Id == opening.Value.Id).ClosingText = tokenRaw;
                    builder.Append($"</x{opening.Value.Id}>");
                }
            }
            else if (token.Type == HtmlTokenType.Character)
            {
                AppendCharacterData(tokenRaw, allocator, items, builder);
            }
            else
            {
                var id = allocator.Next();
                items.Add(MarkupItem.Standalone(id, tokenRaw, "HTML 结构标记"));
                builder.Append($"<x{id}/>");
            }

            position = token.End;
        }

        builder.Append(raw, position, raw.Length - position);
        return new MarkupExtractionResult(builder.ToString(), MarkupExtraction.Serialize(items));
    }

    private static void AppendCharacterData(
        string raw,
        PlaceholderIdAllocator allocator,
        ICollection<MarkupItem> items,
        StringBuilder builder)
    {
        var position = 0;
        foreach (Match match in EntityRegex().Matches(raw))
        {
            builder.Append(raw, position, match.Index - position);
            var id = allocator.Next();
            items.Add(MarkupItem.Standalone(id, match.Value, EntityMeaning(match.Value)));
            builder.Append($"<x{id}/>");
            position = match.Index + match.Length;
        }

        builder.Append(raw, position, raw.Length - position);
    }

    private static (string Name, int Id)? PopOpening(Stack<(string Name, int Id)> openings, string name)
    {
        while (openings.Count > 0)
        {
            var candidate = openings.Pop();
            if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string EntityMeaning(string entity) => entity.Equals("&nbsp;", StringComparison.OrdinalIgnoreCase)
        ? "不间断空格"
        : "HTML 实体";

    [GeneratedRegex(@"&(?:#\d+|#x[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]+);", RegexOptions.CultureInvariant)]
    private static partial Regex EntityRegex();

    private sealed record ParsedHtml(
        IDocument Document,
        IReadOnlyList<HtmlSourceToken> Tokens,
        IReadOnlyDictionary<int, HtmlRange> ElementRanges);

    private sealed record HtmlSourceToken(HtmlTokenType Type, string Name, int Start, int End, bool IsSelfClosing);
    private sealed record HtmlRange(int Start, int End);
    private sealed record MarkupExtractionResult(string SourceText, string MarkupTableJson);
}

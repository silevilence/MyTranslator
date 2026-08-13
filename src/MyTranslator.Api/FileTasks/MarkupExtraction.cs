using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyTranslator.Api.FileTasks;

internal static partial class MarkupExtraction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<MarkupItem> items) =>
        JsonSerializer.Serialize(items, JsonOptions);

    public static bool HasTranslatableText(string sourceText, string markupTableJson)
    {
        using var document = JsonDocument.Parse(markupTableJson);
        var markupIds = document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32())
            .ToHashSet();
        var text = PlaceholderRegex().Replace(
            sourceText,
            match => markupIds.Contains(int.Parse(
                match.Groups["id"].Value,
                System.Globalization.CultureInfo.InvariantCulture))
                    ? string.Empty
                    : match.Value);
        return text.Any(character => !char.IsWhiteSpace(character));
    }

    [GeneratedRegex(@"</?x(?<id>\d+)\s*/?>", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}

internal sealed partial class PlaceholderIdAllocator
{
    private readonly HashSet<int> reservedIds;
    private int nextId = 1;

    public PlaceholderIdAllocator(string source)
    {
        reservedIds = LiteralPlaceholderRegex().Matches(source)
            .Select(match => int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet();
    }

    public int Next()
    {
        while (reservedIds.Contains(nextId))
        {
            nextId++;
        }

        return nextId++;
    }

    [GeneratedRegex(@"</?x(?<id>\d+)\s*/?>", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralPlaceholderRegex();
}

internal sealed class MarkupItem
{
    private MarkupItem(
        int id,
        string kind,
        string? openingText,
        string? closingText,
        string? originalText,
        string meaning)
    {
        Id = id;
        Kind = kind;
        OpeningText = openingText;
        ClosingText = closingText;
        OriginalText = originalText;
        Meaning = meaning;
    }

    public int Id { get; }
    public string Kind { get; }
    public string? OpeningText { get; }
    public string? ClosingText { get; set; }
    public string? OriginalText { get; }
    public string Meaning { get; }

    public static MarkupItem Paired(int id, string opening, string closing, string meaning) =>
        new(id, "paired", opening, closing, null, meaning);

    public static MarkupItem Paired(int id, string opening, string meaning) =>
        new(id, "paired", opening, null, null, meaning);

    public static MarkupItem Standalone(int id, string original, string meaning) =>
        new(id, "standalone", null, null, original, meaning);
}

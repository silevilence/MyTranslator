using System.Text.Json;
using MyTranslator.Api.Text;

namespace MyTranslator.Api.TranslationMemory;

internal static class TranslationMemoryMarkup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ValidateAndCanonicalize(
        JsonElement markupTable,
        string sourceText,
        string? targetText)
    {
        if (markupTable.ValueKind != JsonValueKind.Array)
        {
            throw InvalidMarkup("markupTable must be an array.");
        }

        var items = new List<CanonicalMarkupItem>();
        var ids = new HashSet<int>();
        foreach (var element in markupTable.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("id", out var idValue) ||
                !idValue.TryGetInt32(out var id) ||
                id < 1 ||
                !ids.Add(id) ||
                !element.TryGetProperty("kind", out var kindValue) ||
                kindValue.ValueKind != JsonValueKind.String)
            {
                throw InvalidMarkup("Every markup item must have a unique positive id and a valid kind.");
            }

            var kind = kindValue.GetString();
            var openingText = OptionalString(element, "openingText");
            var closingText = OptionalString(element, "closingText");
            var originalText = OptionalString(element, "originalText");
            var meaning = OptionalString(element, "meaning");
            if (kind == "paired")
            {
                if (string.IsNullOrEmpty(openingText) ||
                    string.IsNullOrEmpty(closingText) ||
                    originalText is not null)
                {
                    throw InvalidMarkup("Paired markup requires openingText and closingText only.");
                }
            }
            else if (kind == "standalone")
            {
                if (string.IsNullOrEmpty(originalText) ||
                    openingText is not null ||
                    closingText is not null)
                {
                    throw InvalidMarkup("Standalone markup requires originalText only.");
                }
            }
            else
            {
                throw InvalidMarkup("kind must be paired or standalone.");
            }

            items.Add(new CanonicalMarkupItem(
                id,
                kind,
                openingText,
                closingText,
                originalText,
                meaning));
        }

        ValidateReferences(sourceText, targetText, items);
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    public static string RemoveRegisteredReferences(string text, string markupTableJson)
    {
        using var document = JsonDocument.Parse(markupTableJson);
        var known = RegisteredTokens(document.RootElement);
        return PlaceholderReferences.Pattern().Replace(
            text,
            match => known.Contains(match.Value) ? " " : match.Value);
    }

    private static void ValidateReferences(
        string sourceText,
        string? targetText,
        IReadOnlyList<CanonicalMarkupItem> items)
    {
        var declarations = items
            .Select(item => new PlaceholderReferences.Declaration(item.Id, item.Kind == "paired"))
            .ToArray();
        var required = PlaceholderReferences.RequiredReferences(declarations);
        var registered = required.ToHashSet(StringComparer.Ordinal);
        var sourceReferences = PlaceholderReferences.RegisteredReferences(sourceText, registered);
        if (!PlaceholderReferences.HaveSameCounts(sourceReferences, required))
        {
            throw PlaceholderViolation();
        }

        ValidateNesting(sourceReferences, items);

        if (targetText is null)
        {
            return;
        }

        var targetReferences = PlaceholderReferences.RegisteredReferences(targetText, registered);
        if (!sourceReferences.SequenceEqual(targetReferences, StringComparer.Ordinal))
        {
            throw PlaceholderViolation();
        }
    }

    private static void ValidateNesting(
        IEnumerable<string> references,
        IReadOnlyList<CanonicalMarkupItem> items)
    {
        var pairedIds = items.Where(item => item.Kind == "paired")
            .Select(item => item.Id)
            .ToHashSet();
        var stack = new Stack<int>();
        foreach (var reference in references)
        {
            if (reference.StartsWith("</x", StringComparison.Ordinal))
            {
                var id = int.Parse(reference.AsSpan(3, reference.Length - 4));
                if (stack.Count == 0 || stack.Pop() != id)
                {
                    throw PlaceholderViolation();
                }
            }
            else if (!reference.EndsWith("/>", StringComparison.Ordinal))
            {
                var id = int.Parse(reference.AsSpan(2, reference.Length - 3));
                if (!pairedIds.Contains(id))
                {
                    throw PlaceholderViolation();
                }

                stack.Push(id);
            }
        }

        if (stack.Count != 0)
        {
            throw PlaceholderViolation();
        }
    }

    private static HashSet<string> RegisteredTokens(JsonElement markupTable) => PlaceholderReferences
        .RequiredReferences(markupTable.EnumerateArray().Select(PlaceholderReferences.DeclarationOf))
        .ToHashSet(StringComparer.Ordinal);

    private static string? OptionalString(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidMarkup($"{property} must be a string or null.");
        }

        return value.GetString();
    }

    private static TranslationMemoryRequestException InvalidMarkup(string message) => new(
        "invalid_tm_markup_table",
        message,
        StatusCodes.Status400BadRequest);

    private static TranslationMemoryRequestException PlaceholderViolation() => new(
        "tm_placeholder_integrity_violation",
        "Source and target placeholder references must match the markup table.",
        StatusCodes.Status422UnprocessableEntity);

    private sealed record CanonicalMarkupItem(
        int Id,
        string Kind,
        string? OpeningText,
        string? ClosingText,
        string? OriginalText,
        string? Meaning);
}

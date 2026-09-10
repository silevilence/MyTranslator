using System.Text;
using System.Text.Json;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.TranslationMemory;

public static class TranslationMemoryEndpoints
{
    public static IEndpointRouteBuilder MapTranslationMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/tm/entries",
                async Task<IResult> (
                    HttpRequest request,
                    TranslationMemoryService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListAsync(
                        RequiredQueryLanguage(request, "sourceLanguage"),
                        RequiredQueryLanguage(request, "targetLanguage"),
                        OptionalQueryText(request),
                        ParseListLimit(request.Query["limit"].ToString()),
                        OptionalQueryValue(request, "cursor"),
                        cancellationToken)))
            .WithName("ListTranslationMemoryEntries")
            .WithTags("Translation Memory");

        endpoints.MapPost(
                "/api/tm/entries",
                async Task<IResult> (
                    JsonElement body,
                    TranslationMemoryService service,
                    CancellationToken cancellationToken) =>
                {
                    var result = await service.CreateAsync(ParseBatch(body), cancellationToken);
                    return Results.Json(
                        result.Response,
                        statusCode: result.CreatedAny
                            ? StatusCodes.Status201Created
                            : StatusCodes.Status200OK);
                })
            .WithName("CreateTranslationMemoryEntries")
            .WithTags("Translation Memory");

        endpoints.MapGet(
                "/api/tm/entries/{entryId:guid}",
                async Task<IResult> (
                    Guid entryId,
                    TranslationMemoryService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.GetAsync(entryId, cancellationToken)))
            .WithName("GetTranslationMemoryEntry")
            .WithTags("Translation Memory");

        endpoints.MapDelete(
                "/api/tm/entries/{entryId:guid}",
                async Task<IResult> (
                    Guid entryId,
                    TranslationMemoryService service,
                    CancellationToken cancellationToken) =>
                {
                    await service.DeleteAsync(entryId, cancellationToken);
                    return Results.NoContent();
                })
            .WithName("DeleteTranslationMemoryEntry")
            .WithTags("Translation Memory");

        endpoints.MapPost(
                "/api/tm/comparisons",
                async Task<IResult> (
                    JsonElement body,
                    TranslationMemoryService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.CompareAsync(
                        ParseComparison(body),
                        cancellationToken)))
            .WithName("CompareTranslationMemoryText")
            .WithTags("Translation Memory");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/segments/{segmentId:guid}/tm-comparison",
                async Task<IResult> (
                    Guid taskId,
                    Guid segmentId,
                    HttpRequest request,
                    TranslationMemoryService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.CompareSegmentAsync(
                        taskId,
                        segmentId,
                        ParsePositiveQueryInteger(request, "extractionRevision", "invalid_extraction_revision"),
                        ParsePositiveQueryInteger(request, "segmentVersion", "invalid_segment_version"),
                        RequiredQueryLanguage(request, "sourceLanguage"),
                        RequiredQueryLanguage(request, "targetLanguage"),
                        ParseComparisonQueryLimit(request.Query["limit"].ToString()),
                        cancellationToken)))
            .WithName("CompareTranslationMemorySegment")
            .WithTags("Translation Memory");

        return endpoints;
    }

    private static string RequiredQueryLanguage(HttpRequest request, string property)
        => NormalizeRequiredLanguage(request.Query[property].ToString(), property);

    private static string? OptionalQueryText(HttpRequest request)
    {
        var value = request.Query["query"].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.EnumerateRunes().Count() > 20_000)
        {
            throw Invalid("invalid_tm_source_text", "query must not exceed 20000 Unicode scalar values.");
        }

        return TranslationMemoryText.NormalizeForSimilarity(normalized);
    }

    private static int ParseListLimit(string value)
    {
        if (value.Length == 0)
        {
            return 100;
        }

        if (!int.TryParse(value, out var limit) || limit is < 1 or > 200)
        {
            throw Invalid("invalid_pagination", "limit must be an integer between 1 and 200.");
        }

        return limit;
    }

    private static int ParseComparisonQueryLimit(string value)
    {
        if (value.Length == 0)
        {
            return 5;
        }

        if (!int.TryParse(value, out var limit) || limit is < 1 or > 20)
        {
            throw Invalid("invalid_match_limit", "limit must be an integer between 1 and 20.");
        }

        return limit;
    }

    private static int ParsePositiveQueryInteger(HttpRequest request, string property, string code)
    {
        if (!int.TryParse(request.Query[property].ToString(), out var value) || value < 1)
        {
            throw Invalid(code, $"{property} must be a positive integer.");
        }

        return value;
    }

    private static string? OptionalQueryValue(HttpRequest request, string name)
    {
        var value = request.Query[name].ToString();
        return value.Length == 0 ? null : value;
    }

    private static IReadOnlyList<CreateTranslationMemoryEntryRequest> ParseBatch(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() is < 1 or > 500)
        {
            throw Invalid("invalid_tm_batch", "items must be an array containing between 1 and 500 entries.");
        }

        return items.EnumerateArray().Select(ParseEntry).ToArray();
    }

    private static TranslationMemoryComparisonRequest ParseComparison(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("invalid_tm_source_text", "The request body must be a JSON object.");
        }

        var sourceLanguage = RequiredLanguage(body, "sourceLanguage");
        var targetLanguage = RequiredLanguage(body, "targetLanguage");
        if (sourceLanguage == targetLanguage)
        {
            throw Invalid("invalid_language_tag", "Source and target languages must differ.");
        }

        return new TranslationMemoryComparisonRequest(
            RequiredText(body, "sourceText", "invalid_tm_source_text"),
            OptionalTargetText(body),
            sourceLanguage,
            targetLanguage,
            OptionalMarkupTable(body),
            OptionalComparisonLimit(body));
    }

    private static string? OptionalTargetText(JsonElement body)
    {
        if (!body.TryGetProperty("targetText", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid("invalid_tm_target_text", "targetText must be null or a non-empty string.");
        }

        return RequiredText(body, "targetText", "invalid_tm_target_text");
    }

    private static int OptionalComparisonLimit(JsonElement body)
    {
        if (!body.TryGetProperty("limit", out var value))
        {
            return 5;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var limit) ||
            limit is < 1 or > 20)
        {
            throw Invalid("invalid_match_limit", "limit must be an integer between 1 and 20.");
        }

        return limit;
    }

    private static CreateTranslationMemoryEntryRequest ParseEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("invalid_tm_batch", "Every item must be a JSON object.");
        }

        var sourceLanguage = RequiredLanguage(item, "sourceLanguage");
        var targetLanguage = RequiredLanguage(item, "targetLanguage");
        if (sourceLanguage == targetLanguage)
        {
            throw Invalid("invalid_language_tag", "Source and target languages must differ.");
        }

        return new CreateTranslationMemoryEntryRequest(
            RequiredText(item, "sourceText", "invalid_tm_source_text"),
            RequiredText(item, "targetText", "invalid_tm_target_text"),
            sourceLanguage,
            targetLanguage,
            OptionalMarkupTable(item));
    }

    private static string RequiredText(JsonElement item, string property, string code)
    {
        if (!item.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(code, $"{property} is required.");
        }

        string normalized;
        try
        {
            normalized = value.GetString()!.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw Invalid(code, $"{property} contains invalid Unicode data.");
        }

        if (normalized.EnumerateRunes().Count() > 20_000)
        {
            throw Invalid(code, $"{property} must not exceed 20000 Unicode scalar values.");
        }

        return normalized;
    }

    private static string RequiredLanguage(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw Invalid("invalid_language_tag", $"{property} must be a valid BCP 47 language tag.");
        }

        return NormalizeRequiredLanguage(value.GetString()!, property);
    }

    private static string NormalizeRequiredLanguage(string value, string property)
    {
        if (!Bcp47LanguageTag.TryNormalize(value.Trim(), out var normalized))
        {
            throw Invalid("invalid_language_tag", $"{property} must be a valid BCP 47 language tag.");
        }

        return normalized;
    }

    private static JsonElement OptionalMarkupTable(JsonElement item)
    {
        if (!item.TryGetProperty("markupTable", out var value))
        {
            using var empty = JsonDocument.Parse("[]");
            return empty.RootElement.Clone();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("invalid_tm_markup_table", "markupTable must be an array.");
        }

        return value.Clone();
    }

    private static TranslationMemoryRequestException Invalid(string code, string message) => new(
        code,
        message,
        StatusCodes.Status400BadRequest);
}

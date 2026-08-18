using System.Text;
using System.Text.Json;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.Terms;

public static class TermEndpoints
{
    public static IEndpointRouteBuilder MapTermEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/terms",
                async Task<IResult> (
                    HttpRequest request,
                    TermService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListAsync(
                        OptionalQueryValue(request, "query"),
                        OptionalQueryValue(request, "sourceLanguage"),
                        OptionalQueryValue(request, "targetLanguage"),
                        ParseLimit(request.Query["limit"].ToString()),
                        OptionalQueryValue(request, "cursor"),
                        cancellationToken)))
            .WithName("ListTerms")
            .WithTags("Terms");

        endpoints.MapPost(
                "/api/terms",
                async Task<IResult> (
                    JsonElement body,
                    TermService service,
                    CancellationToken cancellationToken) =>
                {
                    var term = await service.CreateAsync(ParseCreate(body), cancellationToken);
                    return Results.Created($"/api/terms/{term.Id}", term);
                })
            .WithName("CreateTerm")
            .WithTags("Terms");

        endpoints.MapGet(
                "/api/terms/{termId:guid}",
                async Task<IResult> (
                    Guid termId,
                    TermService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.GetAsync(termId, cancellationToken)))
            .WithName("GetTerm")
            .WithTags("Terms");

        endpoints.MapPut(
                "/api/terms/{termId:guid}",
                async Task<IResult> (
                    Guid termId,
                    JsonElement body,
                    TermService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.UpdateAsync(
                        termId,
                        ParseUpdate(body),
                        cancellationToken)))
            .WithName("UpdateTerm")
            .WithTags("Terms");

        endpoints.MapDelete(
                "/api/terms/{termId:guid}",
                async Task<IResult> (
                    Guid termId,
                    HttpRequest request,
                    TermService service,
                    CancellationToken cancellationToken) =>
                {
                    await service.DeleteAsync(
                        termId,
                        ParseVersion(request.Query["version"].ToString()),
                        cancellationToken);
                    return Results.NoContent();
                })
            .WithName("DeleteTerm")
            .WithTags("Terms");

        endpoints.MapPost(
                "/api/tasks/{taskId:guid}/term-alignment-checks",
                async Task<IResult> (
                    Guid taskId,
                    JsonElement body,
                    TermAlignmentService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.CheckAsync(
                        taskId,
                        ParseAlignment(body),
                        cancellationToken)))
            .WithName("CheckTermAlignment")
            .WithTags("Terms");

        return endpoints;
    }

    private static CreateTermRequest ParseCreate(JsonElement body)
    {
        RequireObject(body);
        return new CreateTermRequest(
            RequiredTerm(body, "sourceTerm", "invalid_source_term"),
            RequiredTerm(body, "targetTerm", "invalid_target_term"),
            RequiredLanguage(body, "sourceLanguage"),
            RequiredLanguage(body, "targetLanguage"),
            OptionalNotes(body),
            OptionalBoolean(body, "caseSensitive", false));
    }

    private static UpdateTermRequest ParseUpdate(JsonElement body)
    {
        RequireObject(body);
        return new UpdateTermRequest(
            RequiredTerm(body, "sourceTerm", "invalid_source_term"),
            RequiredTerm(body, "targetTerm", "invalid_target_term"),
            RequiredLanguage(body, "sourceLanguage"),
            RequiredLanguage(body, "targetLanguage"),
            OptionalNotes(body),
            RequiredBoolean(body, "caseSensitive"),
            RequiredVersion(body));
    }

    private static TermAlignmentRequest ParseAlignment(JsonElement body)
    {
        RequireObject(body);
        if (!body.TryGetProperty("extractionRevision", out var revisionValue) ||
            revisionValue.ValueKind != JsonValueKind.Number ||
            !revisionValue.TryGetInt32(out var extractionRevision) ||
            extractionRevision < 1)
        {
            throw Invalid(
                "invalid_extraction_revision",
                "extractionRevision must be a positive integer.");
        }

        return new TermAlignmentRequest(
            extractionRevision,
            RequiredLanguage(body, "sourceLanguage"),
            RequiredLanguage(body, "targetLanguage"));
    }

    private static string RequiredTerm(JsonElement body, string property, string code)
    {
        if (!body.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(code, $"{property} is required.");
        }

        var normalized = NormalizeNfc(value.GetString()!.Trim(), code);
        if (CountRunes(normalized) > 500)
        {
            throw Invalid(code, $"{property} must not exceed 500 Unicode scalar values.");
        }

        return normalized;
    }

    private static string RequiredLanguage(JsonElement body, string property)
    {
        if (!body.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !Bcp47LanguageTag.TryNormalize(value.GetString()!.Trim(), out var normalized))
        {
            throw Invalid("invalid_language_tag", $"{property} must be a valid BCP 47 language tag.");
        }

        return normalized;
    }

    private static string? OptionalNotes(JsonElement body)
    {
        if (!body.TryGetProperty("notes", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid("invalid_term_notes", "notes must be a string or null.");
        }

        var notes = value.GetString()!.Trim();
        if (notes.Length == 0)
        {
            return null;
        }

        notes = NormalizeNfc(notes, "invalid_term_notes");
        if (CountRunes(notes) > 2000)
        {
            throw Invalid("invalid_term_notes", "notes must not exceed 2000 Unicode scalar values.");
        }

        return notes;
    }

    private static bool OptionalBoolean(JsonElement body, string property, bool defaultValue)
    {
        if (!body.TryGetProperty(property, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(null, $"{property} must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static bool RequiredBoolean(JsonElement body, string property)
    {
        if (!body.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(null, $"{property} is required and must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static int RequiredVersion(JsonElement body)
    {
        if (!body.TryGetProperty("version", out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var version) ||
            version < 1)
        {
            throw Invalid("invalid_term_version", "version must be a positive integer.");
        }

        return version;
    }

    private static int ParseVersion(string value)
    {
        if (!int.TryParse(value, out var version) || version < 1)
        {
            throw Invalid("invalid_term_version", "version must be a positive integer.");
        }

        return version;
    }

    private static int ParseLimit(string value)
    {
        if (value.Length == 0)
        {
            return 100;
        }

        if (!int.TryParse(value, out var limit))
        {
            throw Invalid("invalid_pagination", "limit must be an integer between 1 and 200.");
        }

        return limit;
    }

    private static string? OptionalQueryValue(HttpRequest request, string name)
    {
        var value = request.Query[name].ToString();
        return value.Length == 0 ? null : value;
    }

    private static void RequireObject(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(null, "The request body must be a JSON object.");
        }
    }

    private static string NormalizeNfc(string value, string code)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw Invalid(code, "The text contains invalid Unicode data.");
        }
    }

    private static int CountRunes(string value) => value.EnumerateRunes().Count();

    private static TermRequestException Invalid(string? code, string message) => new(
        code,
        message,
        StatusCodes.Status400BadRequest);
}

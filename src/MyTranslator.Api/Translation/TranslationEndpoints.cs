using System.Text.Json;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Translation;

public static class TranslationEndpoints
{
    public static IEndpointRouteBuilder MapTranslationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/tasks/{taskId:guid}/translation-runs",
                async Task<IResult> (
                    Guid taskId,
                    JsonElement requestBody,
                    TranslationRunService service,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    var request = ParseCreateRequest(requestBody);
                    var run = await service.CreateAsync(taskId, request, cancellationToken);
                    var location = $"/api/tasks/{taskId}/translation-runs/{run.RunId}";
                    httpContext.Response.Headers.RetryAfter = "1";
                    return Results.Accepted(location, run);
                })
            .WithName("CreateTranslationRun")
            .WithTags("Translation");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/translation-runs/{runId:guid}",
                async Task<IResult> (
                    Guid taskId,
                    Guid runId,
                    TranslationRunService service,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    var run = await service.GetAsync(taskId, runId, cancellationToken);
                    if (run is null)
                    {
                        return Problem(new TranslationRequestException(
                            "translation_run_not_found",
                            "Translation run not found",
                            StatusCodes.Status404NotFound));
                    }

                    if (TranslationRunStatusExtensions.ParseWireValue(run.Status).IsActive())
                    {
                        httpContext.Response.Headers.RetryAfter = "1";
                    }

                    return Results.Ok(run);
                })
            .WithName("GetTranslationRun")
            .WithTags("Translation");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/translation-runs",
                async Task<IResult> (
                    Guid taskId,
                    int? limit,
                    string? cursor,
                    TranslationRunService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListAsync(
                        taskId,
                        limit ?? 100,
                        cursor,
                        cancellationToken)))
            .WithName("ListTranslationRuns")
            .WithTags("Translation");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/translation-runs/{runId:guid}/failures",
                async Task<IResult> (
                    Guid taskId,
                    Guid runId,
                    int? limit,
                    string? cursor,
                    TranslationRunService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListFailuresAsync(
                        taskId,
                        runId,
                        limit ?? 100,
                        cursor,
                        cancellationToken)))
            .WithName("ListTranslationRunFailures")
            .WithTags("Translation");

        return endpoints;
    }

    private static CreateTranslationRunRequest ParseCreateRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("extractionRevision", out var revision) ||
            revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt32(out var revisionValue))
        {
            throw new TranslationRequestException(
                "invalid_extraction_revision",
                "The extraction revision must be an integer.",
                StatusCodes.Status400BadRequest);
        }

        string? sourceLanguage = null;
        if (body.TryGetProperty("sourceLanguage", out var source))
        {
            if (source.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                throw InvalidLanguageTag();
            }

            sourceLanguage = source.ValueKind == JsonValueKind.Null ? null : source.GetString();
        }

        if (!body.TryGetProperty("targetLanguage", out var target) ||
            target.ValueKind != JsonValueKind.String)
        {
            throw InvalidLanguageTag();
        }

        var providerId = ParseOptionalId(body, "providerId");
        var modelId = ParseOptionalId(body, "modelId");
        return new CreateTranslationRunRequest(
            revisionValue,
            sourceLanguage,
            target.GetString(),
            providerId,
            modelId);
    }

    private static Guid? ParseOptionalId(JsonElement body, string property)
    {
        if (!body.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id))
        {
            throw new TranslationRequestException(
                "invalid_model_selection",
                $"The {property} must be a UUID or null.",
                StatusCodes.Status400BadRequest);
        }

        return id;
    }

    private static TranslationRequestException InvalidLanguageTag() => new(
        "invalid_language_tag",
        "The language tag is invalid.",
        StatusCodes.Status400BadRequest);

    private static IResult Problem(TranslationRequestException exception) => ApiProblem.Create(
        exception.Code,
        exception.Message,
        exception.StatusCode,
        exception.Errors);
}

using System.Text.Json;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Review;

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/tasks/{taskId:guid}/review-runs",
                async Task<IResult> (
                    Guid taskId,
                    JsonElement requestBody,
                    ReviewRunService service,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    var run = await service.CreateAsync(taskId, ParseCreateRequest(requestBody), cancellationToken);
                    var location = $"/api/tasks/{taskId}/review-runs/{run.RunId}";
                    httpContext.Response.Headers.RetryAfter = "1";
                    return Results.Accepted(location, run);
                })
            .WithName("CreateReviewRun")
            .WithTags("Review");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/review-runs/{runId:guid}",
                async Task<IResult> (
                    Guid taskId,
                    Guid runId,
                    ReviewRunService service,
                    HttpContext httpContext,
                    CancellationToken cancellationToken) =>
                {
                    var run = await service.GetAsync(taskId, runId, cancellationToken);
                    if (run is null)
                    {
                        return Problem(new ReviewRequestException(
                            "review_run_not_found",
                            "Review run not found",
                            StatusCodes.Status404NotFound));
                    }

                    if (ReviewRunStatusExtensions.ParseWireValue(run.Status).IsActive())
                    {
                        httpContext.Response.Headers.RetryAfter = "1";
                    }

                    return Results.Ok(run);
                })
            .WithName("GetReviewRun")
            .WithTags("Review");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/review-runs",
                async Task<IResult> (
                    Guid taskId,
                    int? limit,
                    string? cursor,
                    ReviewRunService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListAsync(taskId, limit ?? 100, cursor, cancellationToken)))
            .WithName("ListReviewRuns")
            .WithTags("Review");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/review-runs/{runId:guid}/failures",
                async Task<IResult> (
                    Guid taskId,
                    Guid runId,
                    int? limit,
                    string? cursor,
                    ReviewRunService service,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await service.ListFailuresAsync(
                        taskId,
                        runId,
                        limit ?? 100,
                        cursor,
                        cancellationToken)))
            .WithName("ListReviewRunFailures")
            .WithTags("Review");

        return endpoints;
    }

    private static CreateReviewRunRequest ParseCreateRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("extractionRevision", out var revision) ||
            revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt32(out var revisionValue))
        {
            throw new ReviewRequestException(
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

        return new CreateReviewRunRequest(
            revisionValue,
            sourceLanguage,
            target.GetString(),
            ParseOptionalId(body, "providerId"),
            ParseOptionalId(body, "modelId"));
    }

    private static Guid? ParseOptionalId(JsonElement body, string property)
    {
        if (!body.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id))
        {
            throw new ReviewRequestException(
                "invalid_model_selection",
                $"The {property} must be a UUID or null.",
                StatusCodes.Status400BadRequest);
        }

        return id;
    }

    private static ReviewRequestException InvalidLanguageTag() => new(
        "invalid_language_tag",
        "The language tag is invalid.",
        StatusCodes.Status400BadRequest);

    private static IResult Problem(ReviewRequestException exception) => ApiProblem.Create(
        exception.Code,
        exception.Message,
        exception.StatusCode,
        exception.Errors);
}

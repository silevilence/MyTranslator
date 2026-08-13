using Microsoft.AspNetCore.Mvc;

namespace MyTranslator.Api.FileTasks;

public static class FileTaskEndpoints
{
    public static IEndpointRouteBuilder MapFileTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/tasks/imports/file",
                async Task<IResult> (
                    IFormFile file,
                    [FromForm] string? fileType,
                    [FromForm] string? segmentationMode,
                    FileTaskService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var result = await service.CreateFileTaskAsync(
                            file,
                            fileType,
                            segmentationMode,
                            cancellationToken);
                        return Results.Created($"/api/tasks/{result.TaskId}", result);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .DisableAntiforgery()
            .WithName("CreateFileImportTask")
            .WithTags("File tasks");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}",
                async Task<IResult> (
                    Guid taskId,
                    FileTaskService service,
                    CancellationToken cancellationToken) =>
                {
                    var task = await service.GetTaskAsync(taskId, cancellationToken);
                    return task is null ? FileTaskProblem.NotFound("task_not_found") : Results.Ok(task);
                })
            .WithName("GetFileTask")
            .WithTags("File tasks");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/segments",
                async Task<IResult> (
                    Guid taskId,
                    int? limit,
                    string? cursor,
                    FileTaskService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var page = await service.GetSegmentsAsync(
                            taskId,
                            limit ?? 100,
                            cursor,
                            cancellationToken);
                        return page is null ? FileTaskProblem.NotFound("task_not_found") : Results.Ok(page);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .WithName("GetFileTaskSegments")
            .WithTags("File tasks");

        endpoints.MapPost(
                "/api/tasks/imports/url",
                async Task<IResult> (
                    UrlImportRequest request,
                    FileTaskService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var result = await service.CreateUrlTaskAsync(request, cancellationToken);
                        return Results.Created($"/api/tasks/{result.TaskId}", result);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .WithName("CreateUrlImportTask")
            .WithTags("File tasks");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/source-units",
                async Task<IResult> (
                    Guid taskId,
                    int? limit,
                    string? cursor,
                    FileTaskService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var page = await service.GetSourceUnitsAsync(
                            taskId,
                            limit ?? 100,
                            cursor,
                            cancellationToken);
                        return page is null ? FileTaskProblem.NotFound("task_not_found") : Results.Ok(page);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .WithName("GetFileTaskSourceUnits")
            .WithTags("File tasks");

        endpoints.MapPost(
                "/api/tasks/{taskId:guid}/extraction-previews",
                async Task<IResult> (
                    Guid taskId,
                    ExtractionPreviewRequest request,
                    FileTaskService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var preview = await service.CreateExtractionPreviewAsync(taskId, request, cancellationToken);
                        return preview is null
                            ? FileTaskProblem.NotFound("task_not_found")
                            : Results.Created(
                                $"/api/tasks/{taskId}/extraction-previews/{preview.PreviewId}",
                                preview);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .WithName("CreateExtractionPreview")
            .WithTags("File tasks");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/extraction-previews/{previewId:guid}",
                async Task<IResult> (
                    Guid taskId,
                    Guid previewId,
                    FileTaskService service) =>
                {
                    try
                    {
                        var preview = await service.GetExtractionPreviewAsync(taskId, previewId);
                        return preview is null
                            ? FileTaskProblem.NotFound("extraction_preview_not_found")
                            : Results.Ok(preview);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .WithName("GetExtractionPreview")
            .WithTags("File tasks");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/extraction-previews/{previewId:guid}/segments",
                async Task<IResult> (
                    Guid taskId,
                    Guid previewId,
                    int? limit,
                    string? cursor,
                    FileTaskService service) =>
                {
                    try
                    {
                        var page = await service.GetExtractionPreviewSegmentsAsync(
                            taskId,
                            previewId,
                            limit ?? 100,
                            cursor);
                        return page is null
                            ? FileTaskProblem.NotFound("extraction_preview_not_found")
                            : Results.Ok(page);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .WithName("GetExtractionPreviewSegments")
            .WithTags("File tasks");

        endpoints.MapPost(
                "/api/tasks/{taskId:guid}/extraction-previews/{previewId:guid}/apply",
                async Task<IResult> (
                    Guid taskId,
                    Guid previewId,
                    ApplyExtractionPreviewRequest request,
                    FileTaskService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var task = await service.ApplyExtractionPreviewAsync(
                            taskId,
                            previewId,
                            request,
                            cancellationToken);
                        return task is null
                            ? FileTaskProblem.NotFound("extraction_preview_not_found")
                            : Results.Ok(task);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .WithName("ApplyExtractionPreview")
            .WithTags("File tasks");

        endpoints.MapGet(
                "/api/tasks/{taskId:guid}/export",
                async Task<IResult> (
                    Guid taskId,
                    FileTaskService service,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var exported = await service.ExportTaskAsync(taskId, cancellationToken);
                        return exported is null
                            ? FileTaskProblem.NotFound("task_not_found")
                            : Results.File(
                                exported.Content,
                                exported.ContentType,
                                exported.FileName);
                    }
                    catch (InvalidFileTaskRequestException exception)
                    {
                        return FileTaskProblem.FromException(exception);
                    }
                })
            .WithName("ExportFileTask")
            .WithTags("File tasks");

        return endpoints;
    }
}

internal static class FileTaskProblem
{
    public static IResult NotFound(string code) => Results.Problem(
        statusCode: 404,
        title: code == "task_not_found" ? "Task not found" : "Extraction preview not found",
        type: $"urn:mytranslator:problem:{code.Replace('_', '-')}",
        extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult FromException(InvalidFileTaskRequestException exception) => Results.Problem(
        statusCode: exception.Code switch
        {
            "import_parse_failed" or
            "dynamic_page_not_supported" or
            "selector_no_match" or
            "target_encoding_unrepresentable" => 422,
            "content_too_large" => 413,
            "source_fetch_failed" => 502,
            "source_fetch_timeout" => 504,
            "extraction_revision_changed" or "task_busy" => 409,
            "reextraction_not_supported" or
            "extraction_preview_stale" or
            "translation_loss_confirmation_required" => 409,
            "task_not_exportable" => 409,
            "extraction_preview_expired" => 410,
            _ => 400
        },
        title: exception.Message,
        type: $"urn:mytranslator:problem:{exception.Code.Replace('_', '-')}",
        extensions: exception.Errors is null
            ? new Dictionary<string, object?> { ["code"] = exception.Code }
            : new Dictionary<string, object?>
            {
                ["code"] = exception.Code,
                ["errors"] = exception.Errors
            });
}

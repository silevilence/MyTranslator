using System.Data;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;
using MyTranslator.Api.FileTasks;
using MyTranslator.Api.Rules;
using MyTranslator.Api.Translation;
using MyTranslator.Api.TranslationMemory;

namespace MyTranslator.Api.TaskOperations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveSegmentRequest(
    int ExtractionRevision, int Version, [property: JsonRequired] string? TargetText,
    string ConfirmationStatus, string? SourceLanguage = null, string? TargetLanguage = null);
public sealed record SegmentVersion(Guid SegmentId, int Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmSegmentsRequest(int ExtractionRevision, [property: JsonRequired] bool Confirmed,
    IReadOnlyList<SegmentVersion>? Items, string? SourceLanguage = null, string? TargetLanguage = null);

public sealed class SegmentEditingService(AppDbContext database, TaskOperationLock taskLock, TranslationMemoryService memory)
{
    public async Task<SegmentResponse> SaveAsync(Guid taskId, Guid segmentId, SaveSegmentRequest request, CancellationToken cancellationToken)
    {
        await using var operation = taskLock.TryAcquire(taskId) ?? throw Problem("task_busy", 409);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var task = await LoadTaskAsync(taskId, request.ExtractionRevision, cancellationToken);
        var segment = task.Segments.SingleOrDefault(item => item.Id == segmentId) ?? throw Problem("segment_not_found", 404);
        await ApplyAsync(task, segment, request.Version, request.TargetText, request.ConfirmationStatus,
            request.SourceLanguage, request.TargetLanguage, cancellationToken);
        UpdateStatus(task);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FileTaskService.ToResponse(segment);
    }

    public async Task<IReadOnlyList<SegmentResponse>> ConfirmAsync(Guid taskId, ConfirmSegmentsRequest request, CancellationToken cancellationToken)
    {
        if (request.Items is null || request.Items.Count is < 1 or > 200 ||
            request.Items.Any(item => item is null) || request.Items.Select(item => item.SegmentId).Distinct().Count() != request.Items.Count)
            throw Problem("invalid_segment_batch", 400);
        await using var operation = taskLock.TryAcquire(taskId) ?? throw Problem("task_busy", 409);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var task = await LoadTaskAsync(taskId, request.ExtractionRevision, cancellationToken);
        var selected = new List<TranslationSegment>();
        foreach (var item in request.Items)
        {
            var segment = task.Segments.SingleOrDefault(segment => segment.Id == item.SegmentId)
                ?? throw Problem("segment_not_found", 404);
            await ApplyAsync(task, segment, item.Version, segment.TargetText,
                request.Confirmed ? "confirmed" : segment.TargetText is null ? "pending" : "translated",
                request.SourceLanguage, request.TargetLanguage, cancellationToken);
            selected.Add(segment);
        }
        UpdateStatus(task);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return selected.Select(FileTaskService.ToResponse).ToArray();
    }

    private async Task<TranslationTask> LoadTaskAsync(Guid taskId, int revision, CancellationToken cancellationToken)
    {
        if (revision < 1) throw Problem("invalid_extraction_revision", 400);
        var task = await database.TranslationTasks.Include(task => task.Segments).ThenInclude(segment => segment.ReviewComments)
            .SingleOrDefaultAsync(task => task.Id == taskId, cancellationToken) ?? throw Problem("task_not_found", 404);
        if (task.ExtractionRevision != revision) throw Problem("extraction_revision_changed", 409);
        if (task.Status == TranslationTaskStatus.Processing ||
            await database.TranslationRuns.AnyAsync(run => run.ActiveTaskLockId == taskId, cancellationToken) ||
            await database.ReviewRuns.AnyAsync(run => run.ActiveTaskLockId == taskId, cancellationToken))
            throw Problem("task_busy", 409);
        return task;
    }

    private async Task ApplyAsync(TranslationTask task, TranslationSegment segment, int version, string? target,
        string status, string? sourceLanguage, string? targetLanguage, CancellationToken cancellationToken)
    {
        if (version < 1) throw Problem("invalid_segment_version", 400);
        if (segment.Version != version)
            throw new SegmentEditingException("segment_version_conflict", 409,
                new Dictionary<string, object?> { ["segmentId"] = segment.Id, ["currentVersion"] = segment.Version });
        target = string.IsNullOrWhiteSpace(target) ? null : target;
        if (status is not ("pending" or "translated" or "confirmed") || (target is null) != (status == "pending"))
            throw Problem("invalid_confirmation_status", 400);
        if (target is not null)
        {
            var context = new TranslationRuleContext(segment.Id, segment.SourceText, target, segment.MarkupTableJson);
            var violation = new PlaceholderIntegrityRule().Evaluate(context) ?? new MissingTranslationRule().Evaluate(context);
            if (violation is not null)
                throw new SegmentEditingException(violation.Code, 422,
                    new Dictionary<string, object?> { ["segmentId"] = segment.Id, ["field"] = "targetText", ["offset"] = violation.Offset, ["length"] = violation.Length });
        }
        var confirmation = SegmentConfirmationStatusExtensions.ParseWireValue(status);
        var changed = target != segment.TargetText || confirmation != segment.ConfirmationStatus;
        if (confirmation == SegmentConfirmationStatus.Confirmed && changed)
        {
            if (string.IsNullOrWhiteSpace(sourceLanguage) || string.IsNullOrWhiteSpace(targetLanguage))
                throw Problem("tm_language_pair_required", 422);
            if (!Bcp47LanguageTag.TryNormalize(sourceLanguage.Trim(), out var source) ||
                !Bcp47LanguageTag.TryNormalize(targetLanguage.Trim(), out var destination) || source == destination)
                throw Problem("invalid_language_tag", 400);
            await memory.AddConfirmedAsync(task, segment, target!, source, destination, cancellationToken);
        }
        if (!changed) return;
        segment.TargetText = target;
        segment.ConfirmationStatus = confirmation;
        segment.Version++;
    }

    private static void UpdateStatus(TranslationTask task)
    {
        if (task.Segments.All(segment => segment.TargetText is not null)) task.Status = TranslationTaskStatus.Completed;
        else if (task.Status != TranslationTaskStatus.Failed) task.Status = TranslationTaskStatus.Created;
    }

    private static SegmentEditingException Problem(string code, int status) => new(code, status);
}

public sealed class SegmentEditingException(string code, int statusCode, IReadOnlyDictionary<string, object?>? errors = null)
    : ApiRequestException(code, code, statusCode, errors);

public static class SegmentEditingEndpoints
{
    public static IEndpointRouteBuilder MapSegmentEditingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/api/tasks/{taskId:guid}/segments/{segmentId:guid}",
                async (Guid taskId, Guid segmentId, SaveSegmentRequest request, SegmentEditingService service, CancellationToken cancellationToken) =>
                    Results.Ok(await service.SaveAsync(taskId, segmentId, request, cancellationToken)))
            .WithName("SaveSegmentTranslation").WithTags("Tasks");
        endpoints.MapPost("/api/tasks/{taskId:guid}/segment-confirmations",
                async (Guid taskId, ConfirmSegmentsRequest request, SegmentEditingService service, CancellationToken cancellationToken) =>
                    Results.Ok(await service.ConfirmAsync(taskId, request, cancellationToken)))
            .WithName("ConfirmSegments").WithTags("Tasks");
        endpoints.MapGet("/api/tasks/{taskId:guid}/result",
                async Task<IResult> (Guid taskId, FileTaskService service, CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var file = await service.ExportTaskAsync(taskId, cancellationToken);
                        return file is null ? FileTaskProblem.NotFound("task_not_found") : Results.File(file.Content, file.ContentType, file.FileName);
                    }
                    catch (InvalidFileTaskRequestException exception) { return FileTaskProblem.FromException(exception); }
                })
            .WithName("GetTaskResult").WithTags("Tasks");
        return endpoints;
    }
}

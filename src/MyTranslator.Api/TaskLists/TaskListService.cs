using System.Data;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;
using MyTranslator.Api.Pagination;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.TaskLists;

public sealed class TaskListService(AppDbContext database)
{
    public async Task<TaskListPage> ListAsync(
        string? status,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (!PaginationLimits.Contains(limit))
        {
            throw new TaskListRequestException(
                "invalid_pagination",
                "The page size must be between 1 and 200.",
                StatusCodes.Status400BadRequest);
        }

        var statusFilter = status switch
        {
            null => (TranslationTaskStatus?)null,
            "created" => TranslationTaskStatus.Created,
            "processing" => TranslationTaskStatus.Processing,
            "completed" => TranslationTaskStatus.Completed,
            "failed" => TranslationTaskStatus.Failed,
            _ => throw new TaskListRequestException(
                "invalid_task_status",
                "The task status is invalid.",
                StatusCodes.Status400BadRequest)
        };
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var taskQuery = database.TranslationTasks.AsNoTracking();
        if (statusFilter is not null)
        {
            taskQuery = taskQuery.Where(task => task.Status == statusFilter);
        }

        var rows = await taskQuery
            .Select(task => new
            {
                task.Id,
                task.Status,
                task.SourceKind,
                task.FileName,
                task.RequestedUrl,
                task.FinalUrl,
                task.MediaType,
                task.ByteLength,
                task.FileType,
                task.ExtractionRevision,
                CompletedSegments = task.Segments.Count(segment => segment.TargetText != null),
                TotalSegments = task.Segments.Count,
                task.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var position = cursor is null ? null : TaskListCursor.Decode(cursor, status);
        var orderedRows = rows
            .OrderByDescending(task => task.CreatedAt)
            .ThenByDescending(task => task.Id);
        var pageRows = (position is null
                ? orderedRows
                : orderedRows.Where(task =>
                    task.CreatedAt < position.CreatedAt ||
                    task.CreatedAt == position.CreatedAt && task.Id.CompareTo(position.TaskId) < 0))
            .Take(limit + 1)
            .ToList();
        var hasNextPage = pageRows.Count > limit;
        pageRows = pageRows.Take(limit).ToList();
        var taskIds = pageRows.Select(task => task.Id).ToArray();
        var runs = await database.TranslationRuns
            .AsNoTracking()
            .Where(run => taskIds.Contains(run.TaskId))
            .ToListAsync(cancellationToken);
        var latestRuns = runs
            .Join(
                pageRows,
                run => new { run.TaskId, run.ExtractionRevision },
                task => new { TaskId = task.Id, task.ExtractionRevision },
                (run, _) => run)
            .GroupBy(run => run.TaskId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(run => run.CreatedAt)
                    .ThenByDescending(run => run.Id)
                    .First());
        var items = pageRows
            .Select(task => new TaskListItemResponse(
                task.Id,
                task.Status.ToWireValue(),
                new TaskListSourceResponse(
                    task.SourceKind,
                    task.FileName,
                    task.RequestedUrl,
                    task.FinalUrl,
                    task.MediaType,
                    task.ByteLength),
                task.FileType,
                task.ExtractionRevision,
                new TaskProgressResponse(task.CompletedSegments, task.TotalSegments),
                latestRuns.GetValueOrDefault(task.Id) is { } latestRun
                    ? ToLatestRunResponse(latestRun)
                    : null,
                task.CreatedAt))
            .ToArray();

        var nextCursor = hasNextPage
            ? TaskListCursor.Encode(status, items[^1].CreatedAt, items[^1].TaskId)
            : null;
        await transaction.CommitAsync(cancellationToken);
        return new TaskListPage(items, nextCursor);
    }

    private static LatestTranslationRunResponse ToLatestRunResponse(TranslationRun run)
    {
        var response = TranslationRunService.ToResponse(run);
        return new LatestTranslationRunResponse(
            response.RunId,
            response.Status,
            response.Progress,
            response.Failure,
            response.CreatedAt,
            response.FinishedAt);
    }
}

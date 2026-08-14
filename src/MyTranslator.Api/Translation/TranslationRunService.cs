using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTranslator.Api.Data;
using MyTranslator.Api.TaskOperations;
using MyTranslator.Api.Pagination;

namespace MyTranslator.Api.Translation;

public sealed class TranslationRunService(
    AppDbContext database,
    IOptions<TranslationOptions> options,
    ITranslationProvider provider,
    TranslationRunQueue queue,
    TaskOperationLock taskOperationLock)
{
    public async Task<TranslationRunResponse> CreateAsync(
        Guid taskId,
        CreateTranslationRunRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRevision(request.ExtractionRevision);
        var sourceLanguage = NormalizeLanguage(request.SourceLanguage, false);
        var targetLanguage = NormalizeLanguage(request.TargetLanguage, true)!;
        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw Problem(
                "unsupported_language_pair",
                "The source and target languages must differ.",
                StatusCodes.Status422UnprocessableEntity);
        }

        await using var operation = await taskOperationLock.AcquireAsync(taskId, cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await AcquireDatabaseWriteLockAsync(taskId, cancellationToken);
        var task = await database.TranslationTasks
            .Include(entity => entity.Segments)
            .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken);
        if (task is null)
        {
            throw Problem("task_not_found", "Task not found", StatusCodes.Status404NotFound);
        }

        if (task.ExtractionRevision != request.ExtractionRevision)
        {
            throw Problem(
                "extraction_revision_changed",
                "The extraction revision changed.",
                StatusCodes.Status409Conflict,
                new Dictionary<string, object?>
                {
                    ["requestedRevision"] = request.ExtractionRevision,
                    ["currentRevision"] = task.ExtractionRevision
                });
        }

        if (task.Status == TranslationTaskStatus.Processing || await database.TranslationRuns.AnyAsync(
                run => run.ActiveTaskLockId == taskId,
                cancellationToken))
        {
            throw Problem("task_busy", "The task is currently processing.", StatusCodes.Status409Conflict);
        }

        if (task.Segments.Any(segment =>
                (segment.TargetText is not null && string.IsNullOrWhiteSpace(segment.TargetText)) ||
                (segment.TargetText is null && segment.ConfirmationStatus != SegmentConfirmationStatus.Pending)))
        {
            throw Problem(
                "invalid_segment_state",
                "A segment has an invalid empty translation.",
                StatusCodes.Status422UnprocessableEntity);
        }

        if (!options.Value.IsConfiguredFor(provider.Name))
        {
            task.Status = TranslationTaskStatus.Failed;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw Problem(
                "llm_not_configured",
                "The LLM provider is not configured.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var selectedSegments = task.Segments.Count(segment => segment.TargetText is null);
        if (selectedSegments == 0)
        {
            task.Status = TranslationTaskStatus.Completed;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw Problem(
                "no_segments_to_translate",
                "The task has no untranslated segments.",
                StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;
        var run = new TranslationRun
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            ActiveTaskLockId = taskId,
            ExtractionRevision = task.ExtractionRevision,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            TotalSegments = task.Segments.Count,
            SelectedSegments = selectedSegments,
            SkippedExistingSegments = task.Segments.Count - selectedSegments,
            CreatedAt = now
        };
        task.Status = TranslationTaskStatus.Processing;
        database.TranslationRuns.Add(run);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsActiveRunConflict(exception))
        {
            throw Problem("task_busy", "The task is currently processing.", StatusCodes.Status409Conflict);
        }

        await transaction.CommitAsync(cancellationToken);
        queue.Enqueue(run.Id);
        return ToResponse(run);
    }

    public async Task<TranslationRunResponse?> GetAsync(
        Guid taskId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await database.TranslationRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Id == runId && entity.TaskId == taskId,
                cancellationToken);
        return run is null ? null : ToResponse(run);
    }

    public async Task<TranslationRunPage> ListAsync(
        Guid taskId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ValidatePagination(limit);
        var revision = await database.TranslationTasks
            .Where(task => task.Id == taskId)
            .Select(task => (int?)task.ExtractionRevision)
            .SingleOrDefaultAsync(cancellationToken);
        if (revision is null)
        {
            throw Problem("task_not_found", "Task not found", StatusCodes.Status404NotFound);
        }

        var resource = $"translation-runs:{taskId}";
        var offset = DecodeCursor(cursor, resource, revision.Value);
        var allRuns = await database.TranslationRuns
            .AsNoTracking()
            .Where(run => run.TaskId == taskId && run.ExtractionRevision == revision.Value)
            .ToListAsync(cancellationToken);
        var runs = allRuns
            .OrderByDescending(run => run.CreatedAt)
            .ThenByDescending(run => run.Id)
            .Skip(offset)
            .Take(limit + 1)
            .ToList();
        var hasNextPage = runs.Count > limit;
        return new TranslationRunPage(
            revision.Value,
            runs.Take(limit).Select(ToResponse).ToArray(),
            hasNextPage ? PaginationCursor.Encode(resource, revision.Value, offset + limit) : null);
    }

    public async Task<TranslationRunFailurePage> ListFailuresAsync(
        Guid taskId,
        Guid runId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ValidatePagination(limit);
        var run = await database.TranslationRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Id == runId && entity.TaskId == taskId,
                cancellationToken);
        if (run is null)
        {
            throw Problem(
                "translation_run_not_found",
                "Translation run not found",
                StatusCodes.Status404NotFound);
        }

        var resource = $"translation-run-failures:{runId}";
        var offset = DecodeCursor(cursor, resource, run.ExtractionRevision);
        var failures = await database.TranslationRunFailures
            .AsNoTracking()
            .Where(failure => failure.RunId == runId)
            .OrderBy(failure => failure.SegmentOrder)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasNextPage = failures.Count > limit;
        return new TranslationRunFailurePage(
            runId,
            failures.Take(limit).Select(failure => new TranslationRunFailureResponse(
                failure.SegmentId,
                failure.SegmentOrder,
                failure.Code,
                failure.Retryable,
                failure.Attempts)).ToArray(),
            hasNextPage ? PaginationCursor.Encode(resource, run.ExtractionRevision, offset + limit) : null);
    }

    internal static TranslationRunResponse ToResponse(TranslationRun run)
    {
        var percent = run.SelectedSegments == 0
            ? 0
            : Math.Round(run.ProcessedSegments * 100.0 / run.SelectedSegments, 1);
        return new TranslationRunResponse(
            run.Id,
            run.TaskId,
            run.ExtractionRevision,
            run.Status.ToWireValue(),
            run.SourceLanguage,
            run.TargetLanguage,
            new TranslationRunSelection(
                run.TotalSegments,
                run.SelectedSegments,
                run.SkippedExistingSegments),
            new TranslationRunProgress(
                run.ProcessedSegments,
                run.SucceededSegments,
                run.FailedSegments,
                percent),
            run.FailureCode is null
                ? null
                : new TranslationRunFailureSummary(
                    run.FailureCode,
                    run.FailureRetryable ?? false,
                    run.FailedSegments),
            run.CreatedAt,
            run.StartedAt,
            run.FinishedAt);
    }

    private static void ValidateRevision(int revision)
    {
        if (revision < 1)
        {
            throw Problem(
                "invalid_extraction_revision",
                "The extraction revision must be at least 1.",
                StatusCodes.Status400BadRequest);
        }
    }

    private static string? NormalizeLanguage(string? value, bool required)
    {
        if (value is null)
        {
            if (!required)
            {
                return null;
            }

            throw Problem(
                "invalid_language_tag",
                "The target language is required.",
                StatusCodes.Status400BadRequest);
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw Problem(
                "invalid_language_tag",
                "The language tag is invalid.",
                StatusCodes.Status400BadRequest);
        }

        if (!Bcp47LanguageTag.TryNormalize(trimmed, out var normalized))
        {
            throw Problem(
                "invalid_language_tag",
                "The language tag is invalid.",
                StatusCodes.Status400BadRequest);
        }

        return normalized;
    }

    private static void ValidatePagination(int limit)
    {
        if (!PaginationLimits.Contains(limit))
        {
            throw Problem(
                "invalid_pagination",
                "The page size must be between 1 and 200.",
                StatusCodes.Status400BadRequest);
        }
    }

    private static int DecodeCursor(string? cursor, string resource, int revision)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            return PaginationCursor.Decode(cursor, resource, revision);
        }
        catch (PaginationCursorException exception)
        {
            throw exception.Error == PaginationCursorError.RevisionChanged
                ? Problem(
                    "extraction_revision_changed",
                    "The extraction revision changed while paging.",
                    StatusCodes.Status409Conflict)
                : Problem(
                    "invalid_cursor",
                    "The pagination cursor is invalid.",
                    StatusCodes.Status400BadRequest);
        }
    }

    private Task<int> AcquireDatabaseWriteLockAsync(Guid taskId, CancellationToken cancellationToken)
    {
        // SQLite serializable transactions start deferred. This no-op UPDATE acquires the writer lock
        // before the extraction revision and active-run checks, closing the re-extraction race window.
        return database.TranslationTasks
            .Where(entity => entity.Id == taskId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(entity => entity.Status, entity => entity.Status),
                cancellationToken);
    }

    private static bool IsActiveRunConflict(DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 2067
        };

    private static TranslationRequestException Problem(
        string code,
        string message,
        int statusCode,
        IReadOnlyDictionary<string, object?>? errors = null) =>
        new(code, message, statusCode, errors);
}

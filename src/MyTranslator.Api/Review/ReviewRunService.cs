using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;
using MyTranslator.Api.Pagination;
using MyTranslator.Api.TaskOperations;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.Review;

public sealed class ReviewRunService(
    AppDbContext database,
    ReviewRunQueue queue,
    TaskOperationLock taskOperationLock)
{
    public async Task<ReviewRunResponse> CreateAsync(
        Guid taskId,
        CreateReviewRunRequest request,
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

        await using var operation = taskOperationLock.TryAcquire(taskId)
            ?? throw Problem("task_busy", "The task is currently processing.", StatusCodes.Status409Conflict);
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
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

        if (task.Status == TranslationTaskStatus.Processing ||
            await database.TranslationRuns.AnyAsync(run => run.ActiveTaskLockId == taskId, cancellationToken) ||
            await database.ReviewRuns.AnyAsync(run => run.ActiveTaskLockId == taskId, cancellationToken))
        {
            throw Problem("task_busy", "The task is currently processing.", StatusCodes.Status409Conflict);
        }

        if (task.Segments.Any(segment =>
                segment.TargetText is not null && string.IsNullOrWhiteSpace(segment.TargetText)))
        {
            throw Problem(
                "invalid_segment_state",
                "A segment has an invalid empty translation.",
                StatusCodes.Status422UnprocessableEntity);
        }

        var selectedSegments = task.Segments
            .Where(segment => segment.TargetText is not null)
            .OrderBy(segment => segment.Order)
            .ToArray();
        if (selectedSegments.Length == 0)
        {
            throw Problem(
                "no_segments_to_review",
                "The task has no translated segments.",
                StatusCodes.Status409Conflict);
        }

        var selection = await ResolveSelectionAsync(request.ProviderId, request.ModelId, cancellationToken);
        var termSnapshot = sourceLanguage is null
            ? Array.Empty<ReviewTermSnapshot>()
            : await database.Terms
                .AsNoTracking()
                .Where(term =>
                    term.SourceLanguage == sourceLanguage &&
                    term.TargetLanguage == targetLanguage)
                .OrderBy(term => term.Id)
                .Select(term => new ReviewTermSnapshot(
                    term.SourceTerm,
                    term.TargetTerm,
                    term.CaseSensitive))
                .ToArrayAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var run = new ReviewRun
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            ActiveTaskLockId = taskId,
            ExtractionRevision = task.ExtractionRevision,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            ProviderId = selection.ProviderId,
            ModelId = selection.ModelId,
            TermSnapshotJson = JsonSerializer.Serialize(termSnapshot),
            TotalSegments = task.Segments.Count,
            SelectedSegments = selectedSegments.Length,
            SkippedUntranslatedSegments = task.Segments.Count - selectedSegments.Length,
            CreatedAt = now,
            Segments = selectedSegments.Select(segment => new ReviewRunSegment
            {
                SegmentId = segment.Id,
                SegmentOrder = segment.Order,
                SourceText = segment.SourceText,
                TargetText = segment.TargetText!,
                MarkupTableJson = segment.MarkupTableJson
            }).ToList()
        };
        database.ReviewRuns.Add(run);
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

    public async Task<ReviewRunResponse?> GetAsync(
        Guid taskId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await database.ReviewRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Id == runId && entity.TaskId == taskId,
                cancellationToken);
        return run is null ? null : ToResponse(run);
    }

    public async Task<ReviewRunPage> ListAsync(
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

        var resource = $"review-runs:{taskId}";
        var offset = DecodeCursor(cursor, resource, revision.Value);
        var allRuns = await database.ReviewRuns
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
        return new ReviewRunPage(
            revision.Value,
            runs.Take(limit).Select(ToResponse).ToArray(),
            hasNextPage ? PaginationCursor.Encode(resource, revision.Value, offset + limit) : null);
    }

    public async Task<ReviewRunFailurePage> ListFailuresAsync(
        Guid taskId,
        Guid runId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ValidatePagination(limit);
        var run = await database.ReviewRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Id == runId && entity.TaskId == taskId,
                cancellationToken);
        if (run is null)
        {
            throw Problem("review_run_not_found", "Review run not found", StatusCodes.Status404NotFound);
        }

        var resource = $"review-run-failures:{runId}";
        var offset = DecodeCursor(cursor, resource, run.ExtractionRevision);
        var failures = await database.ReviewRunFailures
            .AsNoTracking()
            .Where(failure => failure.RunId == runId)
            .OrderBy(failure => failure.SegmentOrder)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasNextPage = failures.Count > limit;
        return new ReviewRunFailurePage(
            runId,
            failures.Take(limit).Select(failure => new ReviewRunFailureResponse(
                failure.SegmentId,
                failure.SegmentOrder,
                failure.Code,
                failure.Retryable,
                failure.Attempts)).ToArray(),
            hasNextPage ? PaginationCursor.Encode(resource, run.ExtractionRevision, offset + limit) : null);
    }

    internal static ReviewRunResponse ToResponse(ReviewRun run) => new(
        run.Id,
        run.TaskId,
        run.ExtractionRevision,
        run.Status.ToWireValue(),
        run.SourceLanguage,
        run.TargetLanguage,
        run.ProviderId,
        run.ModelId,
        new ReviewRunSelection(
            run.TotalSegments,
            run.SelectedSegments,
            run.SkippedUntranslatedSegments),
        ToProgress(run),
        ToFailure(run),
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt);

    internal static ReviewRunProgress ToProgress(ReviewRun run)
    {
        var percent = run.SelectedSegments == 0
            ? 0
            : Math.Round(run.ProcessedSegments * 100.0 / run.SelectedSegments, 1);
        return new ReviewRunProgress(
            run.ProcessedSegments,
            run.SucceededSegments,
            run.FailedSegments,
            percent);
    }

    internal static ReviewRunFailureSummary? ToFailure(ReviewRun run) =>
        run.FailureCode is null
            ? null
            : new ReviewRunFailureSummary(
                run.FailureCode,
                run.FailureRetryable ?? false,
                run.FailedSegments);

    private async Task<AiSelection> ResolveSelectionAsync(
        Guid? requestedProviderId,
        Guid? requestedModelId,
        CancellationToken cancellationToken)
    {
        var explicitProvider = requestedProviderId.HasValue;
        if (!explicitProvider && requestedModelId.HasValue)
        {
            throw Problem(
                "invalid_model_selection",
                "A model cannot be selected without its provider.",
                StatusCodes.Status400BadRequest);
        }

        AiProvider? provider;
        if (explicitProvider)
        {
            provider = await database.Providers
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == requestedProviderId, cancellationToken);
            if (provider is null)
            {
                throw Problem("provider_not_found", "Provider not found.", StatusCodes.Status404NotFound);
            }
        }
        else
        {
            provider = await database.Providers
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.IsDefault, cancellationToken);
            if (provider is null)
            {
                throw NotConfigured(["defaultProvider"]);
            }
        }

        if (!provider.Enabled)
        {
            throw explicitProvider
                ? Problem("provider_disabled", "The selected provider is disabled.", StatusCodes.Status422UnprocessableEntity)
                : NotConfigured(["providerEnabled"]);
        }

        AiModel? model;
        if (requestedModelId.HasValue)
        {
            model = await database.Models
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == requestedModelId && item.ProviderId == provider.Id,
                    cancellationToken);
            if (model is null)
            {
                throw Problem(
                    "model_not_found",
                    "Model not found for the selected provider.",
                    StatusCodes.Status422UnprocessableEntity);
            }
        }
        else
        {
            model = await database.Models
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProviderId == provider.Id && item.IsDefault,
                    cancellationToken);
            if (model is null)
            {
                throw explicitProvider
                    ? Problem(
                        "model_not_found",
                        "The selected provider has no default model.",
                        StatusCodes.Status422UnprocessableEntity)
                    : NotConfigured(["defaultModel"]);
            }
        }

        var missing = AiConfigurationAvailability.GetMissingFields(provider, model);
        if (missing.Count > 0)
        {
            throw NotConfigured(missing);
        }

        return new AiSelection(provider.Id, model.Id);
    }

    private static ReviewRequestException NotConfigured(IReadOnlyList<string> missing) => Problem(
        "llm_not_configured",
        "The selected AI provider and model are not configured completely.",
        StatusCodes.Status503ServiceUnavailable,
        new Dictionary<string, object?> { ["missing"] = missing });

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

        if (!Bcp47LanguageTag.TryNormalize(value.Trim(), out var normalized))
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

    private static bool IsActiveRunConflict(DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 2067
        };

    private static ReviewRequestException Problem(
        string? code,
        string message,
        int statusCode,
        IReadOnlyDictionary<string, object?>? errors = null) =>
        new(code, message, statusCode, errors);

    private sealed record AiSelection(Guid ProviderId, Guid ModelId);
}

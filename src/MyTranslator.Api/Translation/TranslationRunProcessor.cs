using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTranslator.Api.Data;
using MyTranslator.Api.Rules;

namespace MyTranslator.Api.Translation;

public sealed class TranslationRunProcessor(
    AppDbContext database,
    ITranslationProvider provider,
    IEnumerable<ITranslationRule> rules,
    IOptions<TranslationOptions> options)
{
    public async Task ProcessAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await database.TranslationRuns
            .SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken);
        if (run is null || run.Status != TranslationRunStatus.Queued)
        {
            return;
        }

        run.Status = TranslationRunStatus.Processing;
        run.StartedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        while (true)
        {
            var failedIds = database.TranslationRunFailures
                .Where(failure => failure.RunId == runId)
                .Select(failure => failure.SegmentId);
            var segments = await database.TranslationSegments
                .AsNoTracking()
                .Where(segment =>
                    segment.TaskId == run.TaskId &&
                    segment.TargetText == null &&
                    segment.ConfirmationStatus == "pending" &&
                    !failedIds.Contains(segment.Id))
                .OrderBy(segment => segment.Order)
                .Take(Math.Clamp(options.Value.BatchSize, 1, 200))
                .Select(segment => new SegmentWork(
                    segment.Id,
                    segment.Order,
                    segment.SourceText,
                    segment.MarkupTableJson))
                .ToListAsync(cancellationToken);
            if (segments.Count == 0)
            {
                break;
            }

            await ProcessBatchAsync(run, segments, cancellationToken);
        }

        await FinalizeAsync(runId, cancellationToken);
    }

    private async Task ProcessBatchAsync(
        TranslationRun run,
        IReadOnlyList<SegmentWork> batch,
        CancellationToken cancellationToken)
    {
        var pending = batch.ToDictionary(segment => segment.Id);
        var lastFailures = pending.Keys.ToDictionary(
            id => id,
            _ => new FailureCause("llm_response_invalid", true));
        var maxAttempts = Math.Clamp(options.Value.MaxAttempts, 1, 10);

        for (var attempt = 1; attempt <= maxAttempts && pending.Count > 0; attempt++)
        {
            IReadOnlyList<TranslationProviderOutput> outputs;
            try
            {
                outputs = await provider.TranslateAsync(
                    new TranslationProviderRequest(
                        run.SourceLanguage,
                        run.TargetLanguage,
                        pending.Values
                            .OrderBy(segment => segment.Order)
                            .Select(segment => new TranslationProviderSegment(segment.Id, segment.SourceText))
                            .ToArray()),
                    cancellationToken);
            }
            catch (TranslationProviderException exception)
            {
                foreach (var segmentId in pending.Keys)
                {
                    lastFailures[segmentId] = new FailureCause(exception.Code, exception.Retryable);
                }

                if (!exception.Retryable || attempt == maxAttempts)
                {
                    await RecordFailuresAsync(run.Id, pending.Values, lastFailures, attempt, cancellationToken);
                    return;
                }

                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            var outputLookup = outputs
                .GroupBy(output => output.SegmentId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            if (outputLookup.Count != pending.Count ||
                outputLookup.Any(pair => pair.Value.Length != 1 || !pending.ContainsKey(pair.Key)))
            {
                foreach (var segmentId in pending.Keys)
                {
                    lastFailures[segmentId] = new FailureCause("llm_response_invalid", true);
                }

                if (attempt == maxAttempts)
                {
                    await RecordFailuresAsync(run.Id, pending.Values, lastFailures, attempt, cancellationToken);
                    return;
                }

                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            var successful = new Dictionary<Guid, string>();
            foreach (var segment in pending.Values)
            {
                var targetText = outputLookup[segment.Id][0].TargetText;
                if (string.IsNullOrWhiteSpace(targetText))
                {
                    lastFailures[segment.Id] = new FailureCause("llm_response_invalid", true);
                    continue;
                }

                var violation = rules
                    .Select(rule => rule.Evaluate(new TranslationRuleContext(
                        segment.Id,
                        segment.SourceText,
                        targetText,
                        segment.MarkupTableJson)))
                    .FirstOrDefault(result => result is not null);
                if (violation is not null)
                {
                    lastFailures[segment.Id] = new FailureCause(violation.Code, violation.Retryable);
                    continue;
                }

                successful[segment.Id] = targetText;
            }

            if (successful.Count > 0)
            {
                await CommitTranslationsAsync(run.Id, successful, cancellationToken);
                foreach (var segmentId in successful.Keys)
                {
                    pending.Remove(segmentId);
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            if (attempt == maxAttempts)
            {
                await RecordFailuresAsync(run.Id, pending.Values, lastFailures, attempt, cancellationToken);
                return;
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken);
        }
    }

    private async Task CommitTranslationsAsync(
        Guid runId,
        IReadOnlyDictionary<Guid, string> translations,
        CancellationToken cancellationToken)
    {
        var segmentIds = translations.Keys.ToArray();
        var segments = await database.TranslationSegments
            .Where(segment => segmentIds.Contains(segment.Id))
            .ToListAsync(cancellationToken);
        var saved = 0;
        foreach (var segment in segments.Where(segment =>
                     segment.TargetText is null && segment.ConfirmationStatus == "pending"))
        {
            segment.TargetText = translations[segment.Id];
            segment.ConfirmationStatus = "translated";
            segment.Version++;
            saved++;
        }

        var run = await database.TranslationRuns.SingleAsync(entity => entity.Id == runId, cancellationToken);
        run.SucceededSegments += saved;
        run.ProcessedSegments += saved;
        await database.SaveChangesAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    private async Task RecordFailuresAsync(
        Guid runId,
        IEnumerable<SegmentWork> segments,
        IReadOnlyDictionary<Guid, FailureCause> causes,
        int attempts,
        CancellationToken cancellationToken)
    {
        var failures = segments.Select(segment =>
        {
            var cause = causes[segment.Id];
            return new TranslationRunFailure
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                SegmentId = segment.Id,
                SegmentOrder = segment.Order,
                Code = cause.Code,
                Retryable = cause.Retryable,
                Attempts = attempts
            };
        }).ToArray();
        database.TranslationRunFailures.AddRange(failures);
        var run = await database.TranslationRuns.SingleAsync(entity => entity.Id == runId, cancellationToken);
        run.FailedSegments += failures.Length;
        run.ProcessedSegments += failures.Length;
        await database.SaveChangesAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    private async Task FinalizeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await database.TranslationRuns
            .Include(entity => entity.Failures)
            .SingleAsync(entity => entity.Id == runId, cancellationToken);
        var task = await database.TranslationTasks.SingleAsync(entity => entity.Id == run.TaskId, cancellationToken);

        if (run.FailedSegments == 0)
        {
            run.Status = TranslationRunStatus.Completed;
            task.Status = "completed";
        }
        else
        {
            run.Status = run.SucceededSegments > 0
                ? TranslationRunStatus.PartialFailed
                : TranslationRunStatus.Failed;
            task.Status = "failed";
            var codes = run.Failures.Select(failure => failure.Code).Distinct(StringComparer.Ordinal).ToArray();
            run.FailureCode = codes.Length == 1 ? codes[0] : "segment_translation_failed";
            run.FailureRetryable = run.Failures.Any(failure => failure.Retryable);
        }

        run.ProcessedSegments = run.SelectedSegments;
        run.ActiveTaskId = null;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var exponentialDelay = 50 * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * 25;
        return Task.Delay(TimeSpan.FromMilliseconds(exponentialDelay + jitter), cancellationToken);
    }

    private sealed record SegmentWork(Guid Id, int Order, string SourceText, string MarkupTableJson);
    private sealed record FailureCause(string Code, bool Retryable);
}

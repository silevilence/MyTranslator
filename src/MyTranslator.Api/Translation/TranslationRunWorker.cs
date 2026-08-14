using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Translation;

public sealed class TranslationRunWorker(
    IServiceScopeFactory scopeFactory,
    TranslationRunQueue queue,
    ILogger<TranslationRunWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RecoverInterruptedRunsAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<TranslationRunProcessor>();
                await processor.ProcessAsync(runId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Translation run {RunId} failed unexpectedly.", runId);
                await MarkInterruptedAsync(runId, CancellationToken.None);
            }
        }
    }

    private async Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeRuns = await database.TranslationRuns
            .Include(run => run.Failures)
            .Where(run =>
                run.Status == TranslationRunStatus.Queued ||
                run.Status == TranslationRunStatus.Processing)
            .ToListAsync(cancellationToken);
        foreach (var run in activeRuns)
        {
            await MarkInterruptedAsync(database, run, cancellationToken);
        }
    }

    private async Task MarkInterruptedAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await database.TranslationRuns
            .Include(entity => entity.Failures)
            .SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken);
        if (run is not null && run.Status.IsActive())
        {
            await MarkInterruptedAsync(database, run, cancellationToken);
        }
    }

    private static async Task MarkInterruptedAsync(
        AppDbContext database,
        TranslationRun run,
        CancellationToken cancellationToken)
    {
        var failedSegmentIds = run.Failures.Select(failure => failure.SegmentId).ToHashSet();
        var unresolved = await database.TranslationSegments
            .Where(segment => segment.TaskId == run.TaskId && segment.TargetText == null)
            .OrderBy(segment => segment.Order)
            .ToListAsync(cancellationToken);
        foreach (var segment in unresolved.Where(segment => !failedSegmentIds.Contains(segment.Id)))
        {
            database.TranslationRunFailures.Add(new TranslationRunFailure
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                SegmentId = segment.Id,
                SegmentOrder = segment.Order,
                Code = "translation_interrupted",
                Retryable = true,
                Attempts = 0
            });
        }

        run.FailedSegments = run.SelectedSegments - run.SucceededSegments;
        run.ProcessedSegments = run.SelectedSegments;
        run.Status = TranslationRunStatus.Failed;
        run.FailureCode = "translation_interrupted";
        run.FailureRetryable = true;
        run.ActiveTaskId = null;
        run.FinishedAt = DateTimeOffset.UtcNow;
        var task = await database.TranslationTasks.SingleAsync(entity => entity.Id == run.TaskId, cancellationToken);
        task.Status = "failed";
        await database.SaveChangesAsync(cancellationToken);
    }
}

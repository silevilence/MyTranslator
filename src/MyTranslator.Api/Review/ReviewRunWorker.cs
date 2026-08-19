using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Review;

public sealed class ReviewRunWorker(
    IServiceScopeFactory scopeFactory,
    ReviewRunQueue queue,
    IOptions<ReviewOptions> options,
    ILogger<ReviewRunWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RecoverInterruptedRunsAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = Enumerable.Range(0, options.Value.EffectiveMaxConcurrentRuns)
            .Select(_ => ConsumeAsync(stoppingToken));
        await Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<ReviewRunProcessor>();
                await processor.ProcessAsync(runId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Review run {RunId} failed unexpectedly.", runId);
                await MarkInterruptedAsync(runId, CancellationToken.None);
            }
        }
    }

    private async Task RecoverInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeRuns = await database.ReviewRuns
            .Include(run => run.Failures)
            .Include(run => run.Segments)
            .Where(run => run.Status == ReviewRunStatus.Queued || run.Status == ReviewRunStatus.Processing)
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
        var run = await database.ReviewRuns
            .Include(entity => entity.Failures)
            .Include(entity => entity.Segments)
            .SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken);
        if (run is not null && run.Status.IsActive())
        {
            await MarkInterruptedAsync(database, run, cancellationToken);
        }
    }

    private static async Task MarkInterruptedAsync(
        AppDbContext database,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var failedSegmentIds = run.Failures.Select(failure => failure.SegmentId).ToHashSet();
        foreach (var segment in run.Segments.Where(segment =>
                     !segment.Processed && !failedSegmentIds.Contains(segment.SegmentId)))
        {
            database.ReviewRunFailures.Add(new ReviewRunFailure
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                SegmentId = segment.SegmentId,
                SegmentOrder = segment.SegmentOrder,
                Code = "review_interrupted",
                Retryable = true,
                Attempts = 0
            });
            segment.Processed = true;
        }

        run.FailedSegments = run.SelectedSegments - run.SucceededSegments;
        run.ProcessedSegments = run.SelectedSegments;
        run.Status = ReviewRunStatus.Failed;
        run.FailureCode = "review_interrupted";
        run.FailureRetryable = true;
        run.ActiveTaskLockId = null;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }
}

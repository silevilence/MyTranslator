using System.ComponentModel.DataAnnotations.Schema;

namespace MyTranslator.Api.Data;

public sealed class ReviewRun
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public TranslationTask Task { get; set; } = null!;
    [Column("ActiveTaskId")]
    public Guid? ActiveTaskLockId { get; set; }
    public int ExtractionRevision { get; set; }
    public ReviewRunStatus Status { get; set; } = ReviewRunStatus.Queued;
    public string? SourceLanguage { get; set; }
    public string TargetLanguage { get; set; } = null!;
    public Guid ProviderId { get; set; }
    public Guid ModelId { get; set; }
    public string TermSnapshotJson { get; set; } = "[]";
    public int TotalSegments { get; set; }
    public int SelectedSegments { get; set; }
    public int SkippedUntranslatedSegments { get; set; }
    public int ProcessedSegments { get; set; }
    public int SucceededSegments { get; set; }
    public int FailedSegments { get; set; }
    public string? FailureCode { get; set; }
    public bool? FailureRetryable { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<ReviewRunSegment> Segments { get; set; } = [];
    public List<ReviewRunFailure> Failures { get; set; } = [];
    public List<ReviewComment> Comments { get; set; } = [];
}

public enum ReviewRunStatus
{
    Queued,
    Processing,
    Completed,
    PartialFailed,
    Failed
}

public static class ReviewRunStatusExtensions
{
    public static bool IsActive(this ReviewRunStatus status) =>
        status is ReviewRunStatus.Queued or ReviewRunStatus.Processing;

    public static string ToWireValue(this ReviewRunStatus status) => status switch
    {
        ReviewRunStatus.Queued => "queued",
        ReviewRunStatus.Processing => "processing",
        ReviewRunStatus.Completed => "completed",
        ReviewRunStatus.PartialFailed => "partial_failed",
        ReviewRunStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown review run status.")
    };

    public static ReviewRunStatus ParseWireValue(string value) => value switch
    {
        "queued" => ReviewRunStatus.Queued,
        "processing" => ReviewRunStatus.Processing,
        "completed" => ReviewRunStatus.Completed,
        "partial_failed" => ReviewRunStatus.PartialFailed,
        "failed" => ReviewRunStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown review run status '{value}'.")
    };
}

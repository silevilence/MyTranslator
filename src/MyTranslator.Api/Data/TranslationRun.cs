using System.ComponentModel.DataAnnotations.Schema;

namespace MyTranslator.Api.Data;

public sealed class TranslationRun
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public TranslationTask Task { get; set; } = null!;
    [Column("ActiveTaskId")]
    public Guid? ActiveTaskLockId { get; set; }
    public int ExtractionRevision { get; set; }
    public TranslationRunStatus Status { get; set; } = TranslationRunStatus.Queued;
    public string? SourceLanguage { get; set; }
    public string TargetLanguage { get; set; } = null!;
    public Guid? ProviderId { get; set; }
    public Guid? ModelId { get; set; }
    public int TotalSegments { get; set; }
    public int SelectedSegments { get; set; }
    public int SkippedExistingSegments { get; set; }
    public int ProcessedSegments { get; set; }
    public int SucceededSegments { get; set; }
    public int FailedSegments { get; set; }
    public string? FailureCode { get; set; }
    public bool? FailureRetryable { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<TranslationRunFailure> Failures { get; set; } = [];
}

public enum TranslationRunStatus
{
    Queued,
    Processing,
    Completed,
    PartialFailed,
    Failed
}

public static class TranslationRunStatusExtensions
{
    public static bool IsActive(this TranslationRunStatus status) =>
        status is TranslationRunStatus.Queued or TranslationRunStatus.Processing;

    public static string ToWireValue(this TranslationRunStatus status) => status switch
    {
        TranslationRunStatus.Queued => "queued",
        TranslationRunStatus.Processing => "processing",
        TranslationRunStatus.Completed => "completed",
        TranslationRunStatus.PartialFailed => "partial_failed",
        TranslationRunStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown translation run status.")
    };

    public static TranslationRunStatus ParseWireValue(string value) => value switch
    {
        "queued" => TranslationRunStatus.Queued,
        "processing" => TranslationRunStatus.Processing,
        "completed" => TranslationRunStatus.Completed,
        "partial_failed" => TranslationRunStatus.PartialFailed,
        "failed" => TranslationRunStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown translation run status '{value}'.")
    };
}

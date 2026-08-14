namespace MyTranslator.Api.Translation;

public sealed record CreateTranslationRunRequest(
    int ExtractionRevision,
    string? SourceLanguage,
    string? TargetLanguage);

public sealed record TranslationRunSelection(
    int TotalSegments,
    int SelectedSegments,
    int SkippedExistingSegments);

public sealed record TranslationRunProgress(
    int ProcessedSegments,
    int SucceededSegments,
    int FailedSegments,
    double Percent);

public sealed record TranslationRunFailureSummary(
    string Code,
    bool Retryable,
    int FailedSegments);

public sealed record TranslationRunResponse(
    Guid RunId,
    Guid TaskId,
    int ExtractionRevision,
    string Status,
    string? SourceLanguage,
    string TargetLanguage,
    TranslationRunSelection Selection,
    TranslationRunProgress Progress,
    TranslationRunFailureSummary? Failure,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record TranslationRunPage(
    int ExtractionRevision,
    IReadOnlyList<TranslationRunResponse> Items,
    string? NextCursor);

public sealed record TranslationRunFailureResponse(
    Guid SegmentId,
    int SegmentOrder,
    string Code,
    bool Retryable,
    int Attempts);

public sealed record TranslationRunFailurePage(
    Guid RunId,
    IReadOnlyList<TranslationRunFailureResponse> Items,
    string? NextCursor);

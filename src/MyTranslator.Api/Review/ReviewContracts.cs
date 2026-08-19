namespace MyTranslator.Api.Review;

public sealed record CreateReviewRunRequest(
    int ExtractionRevision,
    string? SourceLanguage,
    string? TargetLanguage,
    Guid? ProviderId,
    Guid? ModelId);

public sealed record ReviewRunResponse(
    Guid RunId,
    Guid TaskId,
    int ExtractionRevision,
    string Status,
    string? SourceLanguage,
    string TargetLanguage,
    Guid ProviderId,
    Guid ModelId,
    ReviewRunSelection Selection,
    ReviewRunProgress Progress,
    ReviewRunFailureSummary? Failure,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record ReviewRunSelection(
    int TotalSegments,
    int SelectedSegments,
    int SkippedUntranslatedSegments);

public sealed record ReviewRunProgress(
    int ProcessedSegments,
    int SucceededSegments,
    int FailedSegments,
    double Percent);

public sealed record ReviewRunFailureSummary(string Code, bool Retryable, int FailedSegments);

public sealed record ReviewRunPage(
    int ExtractionRevision,
    IReadOnlyList<ReviewRunResponse> Items,
    string? NextCursor);

public sealed record ReviewRunFailureResponse(
    Guid SegmentId,
    int SegmentOrder,
    string Code,
    bool Retryable,
    int Attempts);

public sealed record ReviewRunFailurePage(
    Guid RunId,
    IReadOnlyList<ReviewRunFailureResponse> Items,
    string? NextCursor);

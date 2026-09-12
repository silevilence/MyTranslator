using MyTranslator.Api.Translation;

namespace MyTranslator.Api.TaskLists;

public sealed record TaskListPage(
    IReadOnlyList<TaskListItemResponse> Items,
    string? NextCursor);

public sealed record TaskListItemResponse(
    Guid TaskId,
    string Status,
    TaskListSourceResponse Source,
    string FileType,
    int ExtractionRevision,
    TaskProgressResponse Progress,
    LatestTranslationRunResponse? LatestTranslationRun,
    DateTimeOffset CreatedAt);

public sealed record TaskListSourceResponse(
    string Kind,
    string FileName,
    string? RequestedUrl,
    string? FinalUrl,
    string MediaType,
    long ByteLength);

public sealed record TaskProgressResponse(int CompletedSegments, int TotalSegments, int ConfirmedSegments = 0)
{
    public double Percent => TotalSegments == 0 ? 100 : Math.Round(100.0 * CompletedSegments / TotalSegments, 1);
    public double ConfirmedPercent => TotalSegments == 0 ? 100 : Math.Round(100.0 * ConfirmedSegments / TotalSegments, 1);
}

public sealed record LatestTranslationRunResponse(
    Guid RunId,
    string Status,
    TranslationRunProgress Progress,
    TranslationRunFailureSummary? Failure,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

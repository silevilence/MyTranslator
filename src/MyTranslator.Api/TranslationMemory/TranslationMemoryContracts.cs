using System.Text.Json;

namespace MyTranslator.Api.TranslationMemory;

public sealed record CreateTranslationMemoryEntryRequest(
    string SourceText,
    string TargetText,
    string SourceLanguage,
    string TargetLanguage,
    JsonElement MarkupTable);

public sealed record TranslationMemoryEntryResponse(
    Guid Id,
    string SourceText,
    string TargetText,
    string SourceLanguage,
    string TargetLanguage,
    JsonElement MarkupTable,
    string Origin,
    Guid? OriginTaskId,
    Guid? OriginSegmentId,
    int? OriginExtractionRevision,
    DateTimeOffset CreatedAt);

public sealed record TranslationMemoryBatchSummary(int Submitted, int Created, int Duplicates);

public sealed record TranslationMemoryBatchItemResponse(
    int Index,
    string Disposition,
    TranslationMemoryEntryResponse Entry);

public sealed record TranslationMemoryBatchResponse(
    TranslationMemoryBatchSummary Summary,
    IReadOnlyList<TranslationMemoryBatchItemResponse> Items);

public sealed record TranslationMemoryBatchResult(TranslationMemoryBatchResponse Response, bool CreatedAny);

public sealed record TranslationMemoryListItemResponse(
    Guid Id,
    string SourceText,
    string TargetText,
    string SourceLanguage,
    string TargetLanguage,
    JsonElement MarkupTable,
    string Origin,
    Guid? OriginTaskId,
    Guid? OriginSegmentId,
    int? OriginExtractionRevision,
    DateTimeOffset CreatedAt,
    double? SourceMatchScore);

public sealed record TranslationMemoryPage(
    IReadOnlyList<TranslationMemoryListItemResponse> Items,
    string? NextCursor);

public sealed record TranslationMemoryComparisonRequest(
    string SourceText,
    string? TargetText,
    string SourceLanguage,
    string TargetLanguage,
    JsonElement MarkupTable,
    int Limit);

public sealed record TranslationMemoryThresholds(
    double MinimumSourceMatchScore,
    double WarningSourceMatchScore,
    double WarningTargetDifference);

public sealed record TranslationMemoryMatchResponse(
    Guid EntryId,
    string SourceText,
    string TargetText,
    JsonElement MarkupTable,
    double SourceMatchScore,
    double? TargetSimilarity,
    double? TargetDifference,
    DateTimeOffset CreatedAt);

public sealed record TranslationMemoryDifferenceWarning(
    bool HasWarning,
    Guid? ReferenceEntryId,
    double? SourceMatchScore,
    double? TargetDifference,
    string? Reason);

public sealed record TranslationMemoryComparisonResponse(
    Guid? TaskId,
    Guid? SegmentId,
    int? ExtractionRevision,
    int? SegmentVersion,
    string SourceLanguage,
    string TargetLanguage,
    TranslationMemoryThresholds Thresholds,
    IReadOnlyList<TranslationMemoryMatchResponse> Items,
    TranslationMemoryDifferenceWarning DifferenceWarning,
    DateTimeOffset ComparedAt);

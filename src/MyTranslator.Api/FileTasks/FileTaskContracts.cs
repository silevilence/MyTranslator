using System.Text.Json;

namespace MyTranslator.Api.FileTasks;

public sealed record UrlImportRequest(string Url, string? FileType);

public sealed record ExtractionPreviewRequest(string Selector);

public sealed record ApplyExtractionPreviewRequest(bool ConfirmTranslationLoss);

public sealed record ExtractionPreviewSummary(
    Guid PreviewId,
    Guid TaskId,
    string Selector,
    int BaseExtractionRevision,
    FileTaskCounts Counts,
    TranslationLossSummary TranslationLoss,
    DateTimeOffset ExpiresAt);

public sealed record TranslationLossSummary(
    int TranslatedSegments,
    int ConfirmedSegments,
    bool RequiresConfirmation);

public sealed record ExportedFile(byte[] Content, string ContentType, string FileName);

public sealed record FileTaskSummary(
    Guid TaskId,
    string Status,
    FileTaskSource Source,
    string FileType,
    string? OriginalEncoding,
    int ExtractionRevision,
    SegmentationSummary? Segmentation,
    FileTaskCounts Counts,
    FileTaskCapabilities Capabilities,
    DateTimeOffset CreatedAt);

public sealed record FileTaskSource(
    string Kind,
    string FileName,
    string? RequestedUrl,
    string? FinalUrl,
    string MediaType,
    long ByteLength);

public sealed record SegmentationSummary(
    string? Requested,
    string Recommended,
    string Effective,
    string Reason);

public sealed record FileTaskCounts(int Segments, int ProtectedBlocks, int Chapters);

public sealed record FileTaskCapabilities(bool CanReextract, bool CanExport);

public sealed record SegmentPage(
    int ExtractionRevision,
    int TotalCount,
    IReadOnlyList<SegmentResponse> Items,
    string? NextCursor);

public sealed record SegmentResponse(
    Guid Id,
    int Order,
    string SourceText,
    string? TargetText,
    string ConfirmationStatus,
    int Version,
    JsonElement MarkupTable,
    JsonElement? Chapter);

public sealed record SourceUnitPage(
    int ExtractionRevision,
    IReadOnlyList<SourceUnitResponse> Items,
    string? NextCursor);

public sealed record SourceUnitResponse(
    int Order,
    string Kind,
    ProtectedBlockResponse? ProtectedBlock,
    SegmentResponse? Segment);

public sealed record ProtectedBlockResponse(
    Guid Id,
    string Type,
    string PreviewText,
    long ByteLength,
    string ContentHash,
    JsonElement? Chapter);

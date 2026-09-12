namespace MyTranslator.Shared.Models;

/// <summary>人工保存只提交可编辑字段及并发上下文。</summary>
public sealed record SaveSegmentRequest(int ExtractionRevision, int Version, string? TargetText,
    string ConfirmationStatus, string? SourceLanguage = null, string? TargetLanguage = null);
/// <summary>批量操作的分段版本。</summary>
public sealed record SegmentVersion(Guid SegmentId, int Version);
/// <summary>一个原子确认批次，最多 200 项。</summary>
public sealed record ConfirmSegmentsRequest(int ExtractionRevision, bool Confirmed,
    IReadOnlyList<SegmentVersion> Items, string? SourceLanguage = null, string? TargetLanguage = null);

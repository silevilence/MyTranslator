namespace MyTranslator.Api.Terms;

public sealed record CreateTermRequest(
    string SourceTerm,
    string TargetTerm,
    string SourceLanguage,
    string TargetLanguage,
    string? Notes,
    bool CaseSensitive);

public sealed record UpdateTermRequest(
    string SourceTerm,
    string TargetTerm,
    string SourceLanguage,
    string TargetLanguage,
    string? Notes,
    bool CaseSensitive,
    int Version);

public record TermResponse(
    Guid Id,
    string SourceTerm,
    string TargetTerm,
    string SourceLanguage,
    string TargetLanguage,
    string? Notes,
    bool CaseSensitive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TermListItemResponse(
    Guid Id,
    string SourceTerm,
    string TargetTerm,
    string SourceLanguage,
    string TargetLanguage,
    string? Notes,
    bool CaseSensitive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    double? MatchScore,
    string? MatchedField);

public sealed record TermPage(IReadOnlyList<TermListItemResponse> Items, string? NextCursor);

public sealed record TermAlignmentRequest(
    int ExtractionRevision,
    string SourceLanguage,
    string TargetLanguage);

public sealed record TermAlignmentResponse(
    Guid TaskId,
    int ExtractionRevision,
    string SourceLanguage,
    string TargetLanguage,
    TermAlignmentSummary Summary,
    IReadOnlyList<UnalignedSegmentResponse> Items,
    DateTimeOffset CheckedAt);

public sealed record TermAlignmentSummary(
    int TotalSegments,
    int CheckedSegments,
    int SkippedUntranslatedSegments,
    int LanguagePairTermCount,
    int MatchedSegments,
    int AlignedSegments,
    int UnalignedSegments);

public sealed record UnalignedSegmentResponse(
    Guid SegmentId,
    int SegmentOrder,
    int SegmentVersion,
    IReadOnlyList<TermMisalignmentResponse> Misalignments);

public sealed record TermMisalignmentResponse(
    Guid TermId,
    string SourceTerm,
    string ExpectedTargetTerm,
    bool CaseSensitive,
    int SourceOccurrences,
    int TargetOccurrences);

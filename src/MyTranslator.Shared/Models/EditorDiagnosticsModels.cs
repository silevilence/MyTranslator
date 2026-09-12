namespace MyTranslator.Shared.Models;

public sealed record RuleFinding(string RuleId, string Code, string Field, int Offset, int Length);
public sealed record SegmentRuleFindings(Guid SegmentId, int Order, int Version, IReadOnlyList<RuleFinding> Violations);
public sealed record RuleCheckResponse(Guid TaskId, int ExtractionRevision, int TotalSegments,
    int ViolatingSegments, IReadOnlyList<string> EnabledRules, IReadOnlyList<SegmentRuleFindings> Items);
public sealed record TermMisalignment(Guid TermId, string SourceTerm, string ExpectedTargetTerm,
    bool CaseSensitive, int SourceOccurrences, int TargetOccurrences);
public sealed record UnalignedSegment(Guid SegmentId, int SegmentOrder, int SegmentVersion, IReadOnlyList<TermMisalignment> Misalignments);
public sealed record TermAlignmentResponse(Guid TaskId, int ExtractionRevision, string SourceLanguage,
    string TargetLanguage, IReadOnlyList<UnalignedSegment> Items);

/// <summary>诊断绑定读取时的修订/版本；旧结果绝不贴到新译文上。</summary>
public static class EditorDiagnostics
{
    public static IReadOnlyList<RuleFinding> RulesFor(RuleCheckResponse? response, Guid taskId, int revision, Segment segment) =>
        response?.TaskId == taskId && response.ExtractionRevision == revision
            ? response.Items.FirstOrDefault(item => item.SegmentId == segment.Id && item.Version == segment.Version)?.Violations ?? [] : [];

    public static IReadOnlyList<TermMisalignment> TermsFor(TermAlignmentResponse? response, Guid taskId, int revision, Segment segment, LanguagePair language) =>
        response?.TaskId == taskId && response.ExtractionRevision == revision &&
        string.Equals(response.SourceLanguage, language.SourceLanguage, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(response.TargetLanguage, language.TargetLanguage, StringComparison.OrdinalIgnoreCase)
            ? response.Items.FirstOrDefault(item => item.SegmentId == segment.Id && item.SegmentVersion == segment.Version)?.Misalignments ?? [] : [];
}

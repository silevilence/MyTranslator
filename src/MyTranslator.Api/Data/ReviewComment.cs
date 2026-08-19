namespace MyTranslator.Api.Data;

public sealed class ReviewComment
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public ReviewRun Run { get; set; } = null!;
    public Guid SegmentId { get; set; }
    public TranslationSegment Segment { get; set; } = null!;
    public int Position { get; set; }
    public string Severity { get; set; } = null!;
    public string Issue { get; set; } = null!;
    public string? Suggestion { get; set; }
}

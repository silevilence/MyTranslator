namespace MyTranslator.Api.Data;

public sealed class ReviewRunSegment
{
    public Guid RunId { get; set; }
    public ReviewRun Run { get; set; } = null!;
    public Guid SegmentId { get; set; }
    public TranslationSegment Segment { get; set; } = null!;
    public int SegmentOrder { get; set; }
    public string SourceText { get; set; } = null!;
    public string TargetText { get; set; } = null!;
    public string MarkupTableJson { get; set; } = "[]";
    public bool Processed { get; set; }
    public bool Succeeded { get; set; }
}

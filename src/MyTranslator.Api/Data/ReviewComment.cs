namespace MyTranslator.Api.Data;

public sealed class ReviewComment
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public ReviewRun Run { get; set; } = null!;
    public Guid SegmentId { get; set; }
    public TranslationSegment Segment { get; set; } = null!;
    public int Position { get; set; }
    public ReviewSeverity Severity { get; set; }
    public string Issue { get; set; } = null!;
    public string? Suggestion { get; set; }
}

public enum ReviewSeverity
{
    High,
    Medium,
    Low
}

public static class ReviewSeverityExtensions
{
    public static string ToWireValue(this ReviewSeverity severity) => severity switch
    {
        ReviewSeverity.High => "high",
        ReviewSeverity.Medium => "medium",
        ReviewSeverity.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown review severity.")
    };

    public static ReviewSeverity ParseWireValue(string value) => value switch
    {
        "high" => ReviewSeverity.High,
        "medium" => ReviewSeverity.Medium,
        "low" => ReviewSeverity.Low,
        _ => throw new InvalidOperationException($"Unknown review severity '{value}'.")
    };
}

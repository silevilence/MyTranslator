namespace MyTranslator.Api.Data;

public sealed class TranslationSegment
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public TranslationTask Task { get; set; } = null!;
    public int Order { get; set; }
    public int SourceUnitOrder { get; set; }
    public string SourceText { get; set; } = null!;
    public string? TargetText { get; set; }
    public SegmentConfirmationStatus ConfirmationStatus { get; set; } = SegmentConfirmationStatus.Pending;
    public int Version { get; set; } = 1;
    public string MarkupTableJson { get; set; } = "[]";
    public string? ChapterJson { get; set; }
    public string TemplateToken { get; set; } = null!;
}

public enum SegmentConfirmationStatus
{
    Pending,
    Translated,
    Confirmed
}

public static class SegmentConfirmationStatusExtensions
{
    public static string ToWireValue(this SegmentConfirmationStatus status) => status switch
    {
        SegmentConfirmationStatus.Pending => "pending",
        SegmentConfirmationStatus.Translated => "translated",
        SegmentConfirmationStatus.Confirmed => "confirmed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown segment confirmation status.")
    };

    public static SegmentConfirmationStatus ParseWireValue(string value) => value switch
    {
        "pending" => SegmentConfirmationStatus.Pending,
        "translated" => SegmentConfirmationStatus.Translated,
        "confirmed" => SegmentConfirmationStatus.Confirmed,
        _ => throw new InvalidOperationException($"Unknown segment confirmation status '{value}'.")
    };
}

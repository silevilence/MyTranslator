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
    public string ConfirmationStatus { get; set; } = "pending";
    public int Version { get; set; } = 1;
    public string MarkupTableJson { get; set; } = "[]";
    public string? ChapterJson { get; set; }
    public string TemplateToken { get; set; } = null!;
}

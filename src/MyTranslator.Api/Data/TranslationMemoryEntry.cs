namespace MyTranslator.Api.Data;

public sealed class TranslationMemoryEntry
{
    public Guid Id { get; set; }
    public string ContentKey { get; set; } = null!;
    public string SourceText { get; set; } = null!;
    public string TargetText { get; set; } = null!;
    public string SourceLanguage { get; set; } = null!;
    public string TargetLanguage { get; set; } = null!;
    public string MarkupTableJson { get; set; } = "[]";
    public string Origin { get; set; } = "external";
    public Guid? OriginTaskId { get; set; }
    public Guid? OriginSegmentId { get; set; }
    public int? OriginExtractionRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedAtSortKey { get; set; } = null!;
}

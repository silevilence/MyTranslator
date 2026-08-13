namespace MyTranslator.Api.Data;

public sealed class ProtectedBlock
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public TranslationTask Task { get; set; } = null!;
    public int SourceUnitOrder { get; set; }
    public string Type { get; set; } = null!;
    public string PreviewText { get; set; } = null!;
    public long ByteLength { get; set; }
    public string ContentHash { get; set; } = null!;
    public string? ChapterJson { get; set; }
}

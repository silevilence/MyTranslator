namespace MyTranslator.Api.Data;

public sealed class TranslationTask
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "created";
    public string SourceKind { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string? RequestedUrl { get; set; }
    public string? FinalUrl { get; set; }
    public string MediaType { get; set; } = null!;
    public long ByteLength { get; set; }
    public string FileType { get; set; } = null!;
    public string? OriginalEncoding { get; set; }
    public byte[] SourceBytes { get; set; } = null!;
    public string ReconstructionTemplate { get; set; } = null!;
    public int ExtractionRevision { get; set; } = 1;
    public string? SegmentationRequested { get; set; }
    public string? SegmentationRecommended { get; set; }
    public string? SegmentationEffective { get; set; }
    public string? SegmentationReason { get; set; }
    public int ProtectedBlockCount { get; set; }
    public int ChapterCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<TranslationSegment> Segments { get; set; } = [];
    public List<ProtectedBlock> ProtectedBlocks { get; set; } = [];
    public List<TranslationRun> TranslationRuns { get; set; } = [];
}

namespace MyTranslator.Api.Data;

public sealed class TranslationTask
{
    public Guid Id { get; set; }
    public TranslationTaskStatus Status { get; set; } = TranslationTaskStatus.Created;
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
    public List<ReviewRun> ReviewRuns { get; set; } = [];
}

public enum TranslationTaskStatus
{
    Created,
    Processing,
    Completed,
    Failed
}

public static class TranslationTaskStatusExtensions
{
    public static string ToWireValue(this TranslationTaskStatus status) => status switch
    {
        TranslationTaskStatus.Created => "created",
        TranslationTaskStatus.Processing => "processing",
        TranslationTaskStatus.Completed => "completed",
        TranslationTaskStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown translation task status.")
    };

    public static bool TryParseWireValue(string value, out TranslationTaskStatus status)
    {
        status = value switch
        {
            "created" => TranslationTaskStatus.Created,
            "processing" => TranslationTaskStatus.Processing,
            "completed" => TranslationTaskStatus.Completed,
            "failed" => TranslationTaskStatus.Failed,
            _ => default
        };
        return value is "created" or "processing" or "completed" or "failed";
    }

    public static TranslationTaskStatus ParseWireValue(string value) =>
        TryParseWireValue(value, out var status)
            ? status
            : throw new InvalidOperationException($"Unknown translation task status '{value}'.");
}

namespace MyTranslator.Api.Data;

public sealed class TranslationRunFailure
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public TranslationRun Run { get; set; } = null!;
    public Guid SegmentId { get; set; }
    public int SegmentOrder { get; set; }
    public string Code { get; set; } = null!;
    public bool Retryable { get; set; }
    public int Attempts { get; set; }
}

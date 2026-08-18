namespace MyTranslator.Api.Data;

public sealed class Term
{
    public Guid Id { get; set; }
    public string SourceTerm { get; set; } = null!;
    public string SourceTermKey { get; set; } = null!;
    public string TargetTerm { get; set; } = null!;
    public string SourceLanguage { get; set; } = null!;
    public string TargetLanguage { get; set; } = null!;
    public string? Notes { get; set; }
    public bool CaseSensitive { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

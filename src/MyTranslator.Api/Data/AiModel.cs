namespace MyTranslator.Api.Data;

public sealed class AiModel
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public AiProvider Provider { get; set; } = null!;
    public string ModelId { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public bool SupportsThinking { get; set; }
    public bool SupportsToolUse { get; set; }
    public bool SupportsStreaming { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

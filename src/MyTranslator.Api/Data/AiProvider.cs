namespace MyTranslator.Api.Data;

public sealed class AiProvider
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public int BatchSize { get; set; } = 20;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; }
    public List<AiModel> Models { get; set; } = [];
}

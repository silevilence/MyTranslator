namespace MyTranslator.Api.AiConfiguration;

public sealed record ProviderWriteRequest(
    string Name,
    string Kind,
    string? BaseUrl,
    string? ApiKey,
    bool Enabled,
    bool IsDefault,
    int BatchSize,
    TimeSpan RequestTimeout,
    int MaxAttempts);

public sealed record ModelWriteRequest(
    string ModelId,
    string DisplayName,
    bool SupportsThinking,
    bool SupportsToolUse,
    bool SupportsStreaming,
    bool IsDefault);

public sealed record AiModelResponse(
    Guid Id,
    Guid ProviderId,
    string ModelId,
    string DisplayName,
    bool SupportsThinking,
    bool SupportsToolUse,
    bool SupportsStreaming,
    bool IsDefault,
    DateTimeOffset CreatedAt);

public sealed record AiProviderResponse(
    Guid Id,
    string Name,
    string Kind,
    string? BaseUrl,
    string? ApiKeyMasked,
    bool Enabled,
    bool IsDefault,
    int BatchSize,
    TimeSpan RequestTimeout,
    int MaxAttempts,
    DateTimeOffset CreatedAt);

public sealed record AiProviderWithModelsResponse(
    Guid Id,
    string Name,
    string Kind,
    string? BaseUrl,
    string? ApiKeyMasked,
    bool Enabled,
    bool IsDefault,
    int BatchSize,
    TimeSpan RequestTimeout,
    int MaxAttempts,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AiModelResponse> Models);

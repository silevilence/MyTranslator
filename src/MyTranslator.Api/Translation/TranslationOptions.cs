namespace MyTranslator.Api.Translation;

public sealed class TranslationOptions
{
    public const int MinimumBatchSize = 1;
    public const int MaximumBatchSize = 200;
    public const int MinimumAttempts = 1;
    public const int MaximumAttempts = 10;
    public const int MinimumConcurrentRuns = 1;
    public const int MaximumConcurrentRuns = 32;

    public string? Provider { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int BatchSize { get; set; } = 20;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxAttempts { get; set; } = 3;
    public int MaxConcurrentRuns { get; set; } = 4;

    public int EffectiveBatchSize => Math.Clamp(BatchSize, MinimumBatchSize, MaximumBatchSize);
    public int EffectiveMaxAttempts => Math.Clamp(MaxAttempts, MinimumAttempts, MaximumAttempts);
    public int EffectiveMaxConcurrentRuns =>
        Math.Clamp(MaxConcurrentRuns, MinimumConcurrentRuns, MaximumConcurrentRuns);

    public bool IsConfiguredFor(string providerName)
    {
        if (!string.Equals(Provider, providerName, StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ApiKey) &&
               !string.IsNullOrWhiteSpace(Model) &&
               BatchSize is >= MinimumBatchSize and <= MaximumBatchSize &&
               RequestTimeout > TimeSpan.Zero &&
               MaxAttempts is >= MinimumAttempts and <= MaximumAttempts &&
               MaxConcurrentRuns is >= MinimumConcurrentRuns and <= MaximumConcurrentRuns;
    }
}

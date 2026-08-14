namespace MyTranslator.Api.Translation;

public sealed class TranslationOptions
{
    public string? Provider { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public int BatchSize { get; set; } = 20;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxAttempts { get; set; } = 3;

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
               BatchSize is >= 1 and <= 200 &&
               RequestTimeout > TimeSpan.Zero &&
               MaxAttempts is >= 1 and <= 10;
    }
}

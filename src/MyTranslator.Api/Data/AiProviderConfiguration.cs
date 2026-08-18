namespace MyTranslator.Api.Data;

public static class AiProviderKinds
{
    public const string OpenAi = "openai";
    public const string Ollama = "ollama";

    public static bool IsSupported(string kind) => kind is OpenAi or Ollama;

    public static bool RequiresApiKey(string kind) => kind == OpenAi;
}

public static class AiProviderRuntimeSettings
{
    public const int MinimumBatchSize = 1;
    public const int MaximumBatchSize = 200;
    public const int DefaultBatchSize = 20;
    public const int MinimumMaxAttempts = 1;
    public const int MaximumMaxAttempts = 10;
    public const int DefaultMaxAttempts = 3;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(1);

    public static bool IsValid(int batchSize, TimeSpan requestTimeout, int maxAttempts) =>
        batchSize is >= MinimumBatchSize and <= MaximumBatchSize &&
        requestTimeout > TimeSpan.Zero &&
        maxAttempts is >= MinimumMaxAttempts and <= MaximumMaxAttempts;

    public static int ClampBatchSize(int batchSize) =>
        Math.Clamp(batchSize, MinimumBatchSize, MaximumBatchSize);

    public static int ClampMaxAttempts(int maxAttempts) =>
        Math.Clamp(maxAttempts, MinimumMaxAttempts, MaximumMaxAttempts);
}

public static class AiConfigurationAvailability
{
    public static IReadOnlyList<string> GetMissingFields(AiProvider provider, AiModel model)
    {
        var missing = new List<string>();
        if (!provider.Enabled)
        {
            missing.Add("providerEnabled");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            missing.Add("baseUrl");
        }

        if (AiProviderKinds.RequiresApiKey(provider.Kind) && string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            missing.Add("apiKey");
        }

        if (string.IsNullOrWhiteSpace(model.ModelId))
        {
            missing.Add("modelId");
        }

        return missing;
    }

    public static bool IsAvailable(AiProvider provider, AiModel model) =>
        GetMissingFields(provider, model).Count == 0;
}

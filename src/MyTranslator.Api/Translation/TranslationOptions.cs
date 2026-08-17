namespace MyTranslator.Api.Translation;

public sealed class TranslationOptions
{
    public const int MinimumConcurrentRuns = 1;
    public const int MaximumConcurrentRuns = 32;

    public int MaxConcurrentRuns { get; set; } = 4;

    public int EffectiveMaxConcurrentRuns =>
        Math.Clamp(MaxConcurrentRuns, MinimumConcurrentRuns, MaximumConcurrentRuns);
}

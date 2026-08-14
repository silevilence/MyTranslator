namespace MyTranslator.Api.Translation;

public interface ITranslationProvider
{
    string Name { get; }

    Task<IReadOnlyList<TranslationProviderOutput>> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken);
}

public sealed record TranslationProviderRequest(
    string? SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<TranslationProviderSegment> Segments);

public sealed record TranslationProviderSegment(Guid SegmentId, string SourceText);

public sealed record TranslationProviderOutput(Guid SegmentId, string TargetText);

public sealed class TranslationProviderException(
    string code,
    bool retryable,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

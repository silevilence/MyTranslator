namespace MyTranslator.Api.Translation;

public sealed class TranslationExecutionException(
    string code,
    bool retryable,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

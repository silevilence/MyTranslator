namespace MyTranslator.Api.Translation;

public sealed class TranslationRequestException(
    string? code,
    string message,
    int statusCode,
    IReadOnlyDictionary<string, object?>? errors = null) : ApiRequestException(code, message, statusCode, errors);

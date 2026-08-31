namespace MyTranslator.Api.TranslationMemory;

public sealed class TranslationMemoryRequestException(
    string? code,
    string message,
    int statusCode,
    IReadOnlyDictionary<string, object?>? errors = null)
    : ApiRequestException(code, message, statusCode, errors);

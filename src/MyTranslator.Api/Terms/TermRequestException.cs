namespace MyTranslator.Api.Terms;

public sealed class TermRequestException(
    string? code,
    string message,
    int statusCode,
    IReadOnlyDictionary<string, object?>? errors = null)
    : ApiRequestException(code, message, statusCode, errors);

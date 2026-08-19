namespace MyTranslator.Api.Review;

public sealed class ReviewRequestException(
    string? code,
    string message,
    int statusCode,
    IReadOnlyDictionary<string, object?>? errors = null) :
    ApiRequestException(code, message, statusCode, errors);

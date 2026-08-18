namespace MyTranslator.Api.AiConfiguration;

public sealed class AiConfigurationRequestException(
    string? code,
    string message,
    int statusCode,
    IReadOnlyDictionary<string, object?>? errors = null) : ApiRequestException(code, message, statusCode, errors);

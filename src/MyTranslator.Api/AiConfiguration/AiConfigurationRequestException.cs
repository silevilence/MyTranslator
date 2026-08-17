namespace MyTranslator.Api.AiConfiguration;

public sealed class AiConfigurationRequestException(
    string code,
    string message,
    int statusCode,
    IReadOnlyDictionary<string, object?>? errors = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public IReadOnlyDictionary<string, object?>? Errors { get; } = errors;
}

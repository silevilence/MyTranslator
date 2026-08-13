namespace MyTranslator.Api.FileTasks;

public sealed class InvalidFileTaskRequestException(
    string code,
    string message,
    Exception? innerException = null,
    IReadOnlyDictionary<string, object?>? errors = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, object?>? Errors { get; } = errors;
}

using MyTranslator.Api.Pagination;

namespace MyTranslator.Api.TaskLists;

internal static class TaskListCursor
{
    private const string Resource = "tasks";

    public static string Encode(string? status, DateTimeOffset createdAt, Guid taskId)
        => OpaqueCursorCodec.Encode(new CursorPayload(Resource, status, createdAt, taskId));

    public static TaskListCursorPosition Decode(string cursor, string? status)
    {
        try
        {
            var payload = OpaqueCursorCodec.Decode<CursorPayload>(cursor);
            if (payload.Resource != Resource ||
                payload.Status != status ||
                payload.TaskId == Guid.Empty)
            {
                throw InvalidCursor();
            }

            return new TaskListCursorPosition(payload.CreatedAt, payload.TaskId);
        }
        catch (TaskListRequestException)
        {
            throw;
        }
        catch (OpaqueCursorCodecException exception)
        {
            throw InvalidCursor(exception);
        }
    }

    private static TaskListRequestException InvalidCursor(Exception? innerException = null) =>
        new("invalid_cursor", "The pagination cursor is invalid.", StatusCodes.Status400BadRequest, innerException);

    private sealed record CursorPayload(
        string Resource,
        string? Status,
        DateTimeOffset CreatedAt,
        Guid TaskId);
}

internal sealed record TaskListCursorPosition(DateTimeOffset CreatedAt, Guid TaskId);

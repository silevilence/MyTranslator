using System.Text;
using System.Text.Json;

namespace MyTranslator.Api.Pagination;

public static class PaginationCursor
{
    public static string Encode(string resource, int revision, int offset)
    {
        var json = JsonSerializer.Serialize(new CursorPayload(resource, revision, offset));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static int Decode(string cursor, string resource, int revision)
    {
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var payload = JsonSerializer.Deserialize<CursorPayload>(
                Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            if (payload is null || payload.Resource != resource || payload.Offset < 0)
            {
                throw new PaginationCursorException(PaginationCursorError.Invalid);
            }

            if (payload.Revision != revision)
            {
                throw new PaginationCursorException(PaginationCursorError.RevisionChanged);
            }

            return payload.Offset;
        }
        catch (PaginationCursorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or JsonException)
        {
            throw new PaginationCursorException(PaginationCursorError.Invalid, exception);
        }
    }

    private sealed record CursorPayload(string Resource, int Revision, int Offset);
}

public enum PaginationCursorError
{
    Invalid,
    RevisionChanged
}

public sealed class PaginationCursorException(
    PaginationCursorError error,
    Exception? innerException = null) : Exception("The pagination cursor is invalid.", innerException)
{
    public PaginationCursorError Error { get; } = error;
}

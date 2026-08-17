namespace MyTranslator.Api.Pagination;

public static class PaginationLimits
{
    public const int Minimum = 1;
    public const int Maximum = 200;

    public static bool Contains(int limit) => limit is >= Minimum and <= Maximum;
}

public static class PaginationCursor
{
    public static string Encode(string resource, int revision, int offset)
        => OpaqueCursorCodec.Encode(new CursorPayload(resource, revision, offset));

    public static int Decode(string cursor, string resource, int revision)
    {
        try
        {
            var payload = OpaqueCursorCodec.Decode<CursorPayload>(cursor);
            if (payload.Resource != resource || payload.Offset < 0)
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
        catch (OpaqueCursorCodecException exception)
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

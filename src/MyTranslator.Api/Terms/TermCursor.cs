using MyTranslator.Api.Pagination;

namespace MyTranslator.Api.Terms;

internal static class TermCursor
{
    private const string Resource = "terms";

    public static string Encode(
        string? query,
        string? sourceLanguage,
        string? targetLanguage,
        double? matchScore,
        DateTimeOffset updatedAt,
        Guid termId) => OpaqueCursorCodec.Encode(new CursorPayload(
        Resource,
        query,
        sourceLanguage,
        targetLanguage,
        matchScore,
        updatedAt,
        termId));

    public static TermCursorPosition Decode(
        string cursor,
        string? query,
        string? sourceLanguage,
        string? targetLanguage)
    {
        try
        {
            var payload = OpaqueCursorCodec.Decode<CursorPayload>(cursor);
            if (payload.Resource != Resource ||
                payload.Query != query ||
                payload.SourceLanguage != sourceLanguage ||
                payload.TargetLanguage != targetLanguage ||
                payload.TermId == Guid.Empty ||
                (query is null) != (payload.MatchScore is null))
            {
                throw InvalidCursor();
            }

            return new TermCursorPosition(payload.MatchScore, payload.UpdatedAt, payload.TermId);
        }
        catch (TermRequestException)
        {
            throw;
        }
        catch (OpaqueCursorCodecException)
        {
            throw InvalidCursor();
        }
    }

    private static TermRequestException InvalidCursor() => new(
        "invalid_cursor",
        "The pagination cursor is invalid.",
        StatusCodes.Status400BadRequest);

    private sealed record CursorPayload(
        string Resource,
        string? Query,
        string? SourceLanguage,
        string? TargetLanguage,
        double? MatchScore,
        DateTimeOffset UpdatedAt,
        Guid TermId);
}

internal sealed record TermCursorPosition(double? MatchScore, DateTimeOffset UpdatedAt, Guid TermId);

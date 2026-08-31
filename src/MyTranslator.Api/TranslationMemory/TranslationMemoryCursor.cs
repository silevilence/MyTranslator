using MyTranslator.Api.Pagination;

namespace MyTranslator.Api.TranslationMemory;

internal static class TranslationMemoryCursor
{
    private const string Resource = "tm-entries";

    public static string Encode(
        string? query,
        string sourceLanguage,
        string targetLanguage,
        double? sourceMatchScore,
        string createdAtSortKey,
        Guid entryId) => OpaqueCursorCodec.Encode(new CursorPayload(
        Resource,
        query,
        sourceLanguage,
        targetLanguage,
        sourceMatchScore,
        createdAtSortKey,
        entryId));

    public static TranslationMemoryCursorPosition Decode(
        string cursor,
        string? query,
        string sourceLanguage,
        string targetLanguage)
    {
        try
        {
            var payload = OpaqueCursorCodec.Decode<CursorPayload>(cursor);
            if (payload.Resource != Resource ||
                payload.Query != query ||
                payload.SourceLanguage != sourceLanguage ||
                payload.TargetLanguage != targetLanguage ||
                payload.EntryId == Guid.Empty ||
                string.IsNullOrWhiteSpace(payload.CreatedAtSortKey) ||
                (query is null) != (payload.SourceMatchScore is null))
            {
                throw InvalidCursor();
            }

            return new TranslationMemoryCursorPosition(
                payload.SourceMatchScore,
                payload.CreatedAtSortKey,
                payload.EntryId);
        }
        catch (OpaqueCursorCodecException)
        {
            throw InvalidCursor();
        }
    }

    private static TranslationMemoryRequestException InvalidCursor() => new(
        "invalid_cursor",
        "The pagination cursor is invalid.",
        StatusCodes.Status400BadRequest);

    private sealed record CursorPayload(
        string Resource,
        string? Query,
        string SourceLanguage,
        string TargetLanguage,
        double? SourceMatchScore,
        string CreatedAtSortKey,
        Guid EntryId);
}

internal sealed record TranslationMemoryCursorPosition(
    double? SourceMatchScore,
    string CreatedAtSortKey,
    Guid EntryId);

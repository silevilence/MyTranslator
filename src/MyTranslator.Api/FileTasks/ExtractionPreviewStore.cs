using System.Collections.Concurrent;

namespace MyTranslator.Api.FileTasks;

public sealed class ExtractionPreviewStore
{
    private readonly ConcurrentDictionary<Guid, ExtractionPreviewData> _previews = new();

    internal void Add(ExtractionPreviewData preview) => _previews[preview.Id] = preview;

    internal bool TryGet(Guid previewId, out ExtractionPreviewData preview) =>
        _previews.TryGetValue(previewId, out preview!);

    internal void Remove(Guid previewId) => _previews.TryRemove(previewId, out _);
}

internal sealed record ExtractionPreviewData(
    Guid Id,
    Guid TaskId,
    string Selector,
    int BaseExtractionRevision,
    IReadOnlyList<PreviewDocumentData> Documents,
    IReadOnlyList<PreviewSegmentData> Segments,
    int ProtectedBlockCount,
    int TranslatedSegmentCount,
    int ConfirmedSegmentCount,
    DateTimeOffset ExpiresAt);

internal sealed record PreviewSegmentData(
    Guid Id,
    int Order,
    string SourceText,
    string MarkupTableJson,
    string? ChapterJson);

internal sealed record PreviewDocumentData(
    string? ResourcePath,
    string Encoding,
    string? ChapterJson,
    TextExtractionResult Extraction);

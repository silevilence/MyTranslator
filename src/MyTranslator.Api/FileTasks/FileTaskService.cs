using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;
using MyTranslator.Api.TaskOperations;
using MyTranslator.Api.Pagination;

namespace MyTranslator.Api.FileTasks;

public sealed class FileTaskService(
    AppDbContext database,
    UrlImportClient urlImportClient,
    ExtractionPreviewStore previewStore,
    TaskOperationLock taskOperationLock)
{
    private const long MaximumUploadBytes = 10L * 1024 * 1024;

    public async Task<FileTaskSummary> CreateFileTaskAsync(
        IFormFile file,
        string? fileType,
        string? segmentationMode,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            throw new InvalidFileTaskRequestException("import_parse_failed", "The uploaded file is empty.");
        }

        var effectiveFileType = ResolveFileType(file.FileName, file.ContentType, fileType);
        if (file.Length > MaximumUploadBytes)
        {
            throw new InvalidFileTaskRequestException("content_too_large", "The uploaded file is too large.");
        }

        await using var source = file.OpenReadStream();
        await using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaximumUploadBytes)
            {
                throw new InvalidFileTaskRequestException("content_too_large", "The uploaded file is too large.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var sourceBytes = memory.ToArray();
        if (effectiveFileType != "txt" && segmentationMode is not null)
        {
            throw new InvalidFileTaskRequestException(
                "segmentation_mode_not_applicable",
                "Segmentation mode only applies to txt files.");
        }

        TranslationTask task;
        if (effectiveFileType == "epub")
        {
            task = BuildEpubTask(file, sourceBytes, EpubExtraction.Extract(sourceBytes));
        }
        else
        {
            var extraction = effectiveFileType switch
            {
                "txt" => TextExtraction.Extract(sourceBytes, segmentationMode),
                "markdown" => MarkdownExtraction.Extract(sourceBytes),
                "html" => HtmlExtraction.Extract(sourceBytes),
                _ => throw new InvalidFileTaskRequestException(
                    "invalid_file_type",
                    "This file type is not implemented yet.")
            };
            task = BuildTextTask(
                "file",
                Path.GetFileName(file.FileName),
                file.ContentType,
                null,
                null,
                effectiveFileType,
                sourceBytes,
                extraction);
        }

        database.TranslationTasks.Add(task);
        await database.SaveChangesAsync(cancellationToken);
        return ToSummary(task);
    }

    public async Task<FileTaskSummary> CreateUrlTaskAsync(
        UrlImportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.FileType is not null && !request.FileType.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidFileTaskRequestException(
                "invalid_file_type_for_url",
                "URL imports only support html.");
        }

        var fetched = await urlImportClient.FetchAsync(request.Url, cancellationToken);
        var extraction = HtmlExtraction.Extract(
            fetched.Bytes,
            declaredEncoding: fetched.DeclaredEncoding);
        if (!extraction.Units.Any(unit => unit.IsSegment) &&
            extraction.OriginalText.Contains("<script", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidFileTaskRequestException(
                "dynamic_page_not_supported",
                "The page requires JavaScript to produce translatable content.");
        }

        var fileName = Path.GetFileName(fetched.FinalUrl.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "translated-page.html";
        }

        var task = BuildTextTask(
            "url",
            fileName,
            fetched.MediaType,
            request.Url,
            fetched.FinalUrl.AbsoluteUri,
            "html",
            fetched.Bytes,
            extraction);
        database.TranslationTasks.Add(task);
        await database.SaveChangesAsync(cancellationToken);
        return ToSummary(task);
    }

    public async Task<FileTaskSummary?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await database.TranslationTasks
            .AsNoTracking()
            .Include(entity => entity.Segments)
            .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken);
        return task is null ? null : ToSummary(task);
    }

    public async Task<SegmentPage?> GetSegmentsAsync(
        Guid taskId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            throw new InvalidFileTaskRequestException("invalid_pagination", "The page limit must be between 1 and 200.");
        }

        var task = await database.TranslationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var cursorResource = $"segments:{taskId}";
        var offset = DecodeCursor(cursor, cursorResource, task.ExtractionRevision);
        var totalCount = await database.TranslationSegments
            .CountAsync(entity => entity.TaskId == taskId, cancellationToken);
        var segments = await database.TranslationSegments
            .AsNoTracking()
            .Where(entity => entity.TaskId == taskId)
            .OrderBy(entity => entity.Order)
            .Skip(offset)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasNextPage = segments.Count > limit;
        if (hasNextPage)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return new SegmentPage(
            task.ExtractionRevision,
            totalCount,
            segments.Select(ToResponse).ToList(),
            hasNextPage ? PaginationCursor.Encode(cursorResource, task.ExtractionRevision, offset + limit) : null);
    }

    public async Task<SourceUnitPage?> GetSourceUnitsAsync(
        Guid taskId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            throw new InvalidFileTaskRequestException("invalid_pagination", "The page limit must be between 1 and 200.");
        }

        var task = await database.TranslationTasks
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        var cursorResource = $"source-units:{taskId}";
        var offset = DecodeCursor(cursor, cursorResource, task.ExtractionRevision);
        var segments = await database.TranslationSegments
            .AsNoTracking()
            .Where(entity => entity.TaskId == taskId)
            .ToListAsync(cancellationToken);
        var protectedBlocks = await database.ProtectedBlocks
            .AsNoTracking()
            .Where(entity => entity.TaskId == taskId)
            .ToListAsync(cancellationToken);
        var units = segments
            .Select(segment => new SourceUnitResponse(
                segment.SourceUnitOrder,
                "segment",
                null,
                ToResponse(segment)))
            .Concat(protectedBlocks.Select(block => new SourceUnitResponse(
                block.SourceUnitOrder,
                "protectedBlock",
                ToResponse(block),
                null)))
            .OrderBy(unit => unit.Order)
            .ToList();
        var page = units.Skip(offset).Take(limit + 1).ToList();
        var hasNextPage = page.Count > limit;
        if (hasNextPage)
        {
            page.RemoveAt(page.Count - 1);
        }

        return new SourceUnitPage(
            task.ExtractionRevision,
            page,
            hasNextPage ? PaginationCursor.Encode(cursorResource, task.ExtractionRevision, offset + limit) : null);
    }

    public async Task<ExtractionPreviewSummary?> CreateExtractionPreviewAsync(
        Guid taskId,
        ExtractionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var task = await database.TranslationTasks
            .AsNoTracking()
            .Include(entity => entity.Segments)
            .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        if (task.FileType is not ("html" or "epub"))
        {
            throw new InvalidFileTaskRequestException(
                "reextraction_not_supported",
                "Re-extraction is only supported for HTML-like tasks.");
        }

        if (string.IsNullOrWhiteSpace(request.Selector))
        {
            throw new InvalidFileTaskRequestException("invalid_selector", "The CSS selector is required.");
        }

        IReadOnlyList<PreviewDocumentData> documents;
        if (task.FileType == "epub")
        {
            var epub = EpubExtraction.Extract(task.SourceBytes, request.Selector);
            documents = epub.Documents.Select(document => new PreviewDocumentData(
                document.ResourcePath,
                document.Extraction.EncodingName,
                BuildChapterJson(document),
                document.Extraction)).ToList();
        }
        else
        {
            var html = HtmlExtraction.Extract(task.SourceBytes, request.Selector);
            documents = [new PreviewDocumentData(null, html.EncodingName, null, html)];
        }

        var segments = new List<PreviewSegmentData>();
        foreach (var document in documents)
        {
            foreach (var unit in document.Extraction.Units.Where(unit => unit.IsSegment))
            {
                segments.Add(new PreviewSegmentData(
                    Guid.NewGuid(),
                    segments.Count + 1,
                    unit.Text,
                    unit.MarkupTableJson,
                    document.ChapterJson));
            }
        }
        var translatedCount = task.Segments.Count(segment => !string.IsNullOrWhiteSpace(segment.TargetText));
        var confirmedCount = task.Segments.Count(segment => segment.ConfirmationStatus == "confirmed");
        var preview = new ExtractionPreviewData(
            Guid.NewGuid(),
            task.Id,
            request.Selector,
            task.ExtractionRevision,
            documents,
            segments,
            documents.Sum(document =>
                document.Extraction.Units.Count(unit => !unit.IsSegment && unit.Text.Length > 0)),
            translatedCount,
            confirmedCount,
            DateTimeOffset.UtcNow.AddMinutes(15));
        previewStore.Add(preview);
        return ToSummary(preview);
    }

    public Task<ExtractionPreviewSummary?> GetExtractionPreviewAsync(
        Guid taskId,
        Guid previewId)
    {
        var preview = GetPreview(taskId, previewId);
        return Task.FromResult(preview is null ? null : ToSummary(preview));
    }

    public Task<SegmentPage?> GetExtractionPreviewSegmentsAsync(
        Guid taskId,
        Guid previewId,
        int limit,
        string? cursor)
    {
        if (limit is < 1 or > 200)
        {
            throw new InvalidFileTaskRequestException("invalid_pagination", "The page limit must be between 1 and 200.");
        }

        var preview = GetPreview(taskId, previewId);
        if (preview is null)
        {
            return Task.FromResult<SegmentPage?>(null);
        }

        var cursorResource = $"preview-segments:{previewId}";
        var offset = DecodeCursor(cursor, cursorResource, preview.BaseExtractionRevision);
        var page = preview.Segments.Skip(offset).Take(limit + 1).ToList();
        var hasNextPage = page.Count > limit;
        if (hasNextPage)
        {
            page.RemoveAt(page.Count - 1);
        }

        var items = page.Select(ToResponse).ToList();
        return Task.FromResult<SegmentPage?>(new SegmentPage(
            preview.BaseExtractionRevision,
            preview.Segments.Count,
            items,
            hasNextPage ? PaginationCursor.Encode(cursorResource, preview.BaseExtractionRevision, offset + limit) : null));
    }

    public async Task<FileTaskSummary?> ApplyExtractionPreviewAsync(
        Guid taskId,
        Guid previewId,
        ApplyExtractionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        await using var operation = await taskOperationLock.AcquireAsync(taskId, cancellationToken);
        var preview = GetPreview(taskId, previewId);
        if (preview is null)
        {
            return null;
        }

        var task = await database.TranslationTasks
            .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }

        if (task.ExtractionRevision != preview.BaseExtractionRevision)
        {
            throw new InvalidFileTaskRequestException(
                "extraction_preview_stale",
                "The extraction preview is stale.");
        }

        if (task.Status == "processing")
        {
            throw new InvalidFileTaskRequestException("task_busy", "The task is currently processing.");
        }

        var translatedCount = await database.TranslationSegments.CountAsync(
            segment => segment.TaskId == taskId &&
                       segment.TargetText != null &&
                       segment.TargetText.Trim() != string.Empty,
            cancellationToken);
        var confirmedCount = await database.TranslationSegments.CountAsync(
            segment => segment.TaskId == taskId && segment.ConfirmationStatus == "confirmed",
            cancellationToken);
        if ((translatedCount > 0 || confirmedCount > 0) && !request.ConfirmTranslationLoss)
        {
            throw new InvalidFileTaskRequestException(
                "translation_loss_confirmation_required",
                "Applying the preview will discard existing translations.",
                errors: new Dictionary<string, object?>
                {
                    ["translatedSegments"] = translatedCount,
                    ["confirmedSegments"] = confirmedCount
                });
        }

        await database.TranslationRuns
            .Where(run => run.TaskId == taskId)
            .ExecuteDeleteAsync(cancellationToken);
        await database.TranslationSegments
            .Where(segment => segment.TaskId == taskId)
            .ExecuteDeleteAsync(cancellationToken);
        await database.ProtectedBlocks
            .Where(block => block.TaskId == taskId)
            .ExecuteDeleteAsync(cancellationToken);
        var segmentOrder = 0;
        var sourceUnitOrder = 0;
        var templates = new List<EpubTemplate>();
        string? textTemplate = null;
        foreach (var document in preview.Documents)
        {
            var template = new StringBuilder();
            AppendUnits(
                task,
                document.Extraction.Units,
                template,
                ref segmentOrder,
                ref sourceUnitOrder,
                document.ChapterJson,
                document.Encoding);
            if (document.ResourcePath is null)
            {
                textTemplate = template.ToString();
            }
            else
            {
                templates.Add(new EpubTemplate(document.ResourcePath, document.Encoding, template.ToString()));
            }
        }

        database.TranslationSegments.AddRange(task.Segments);
        database.ProtectedBlocks.AddRange(task.ProtectedBlocks);
        task.ReconstructionTemplate = task.FileType == "epub"
            ? JsonSerializer.Serialize(templates, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : textTemplate!;
        task.ProtectedBlockCount = task.ProtectedBlocks.Count;
        task.ChapterCount = preview.Documents.Count(document => document.ChapterJson is not null);
        task.ExtractionRevision++;
        task.Status = "created";
        await database.SaveChangesAsync(cancellationToken);
        previewStore.Remove(previewId);
        return ToSummary(task);
    }

    public async Task<ExportedFile?> ExportTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var operation = await taskOperationLock.AcquireAsync(taskId, cancellationToken);
        var task = await database.TranslationTasks
            .AsNoTracking()
            .Include(entity => entity.Segments)
            .SingleOrDefaultAsync(entity => entity.Id == taskId, cancellationToken);
        if (task is null)
        {
            return null;
        }


        if (task.Status == "processing")
        {
            throw new InvalidFileTaskRequestException("task_busy", "The task is currently processing.");
        }

        if (task.Segments.Any(segment => string.IsNullOrWhiteSpace(segment.TargetText)))
        {
            var missingTranslations = task.Segments.Count(segment => string.IsNullOrWhiteSpace(segment.TargetText));
            throw new InvalidFileTaskRequestException(
                "task_not_exportable",
                "All segments must have translations before export.",
                errors: new Dictionary<string, object?>
                {
                    ["missingTranslations"] = missingTranslations,
                    ["placeholderViolations"] = 0
                });
        }

        var content = task.FileType == "epub"
            ? ExportEpub(task)
            : EncodeText(
                RenderTemplate(task.ReconstructionTemplate, task.Segments),
                task.SourceBytes,
                task.OriginalEncoding);
        var contentType = task.FileType switch
        {
            "txt" => $"text/plain; charset={task.OriginalEncoding}",
            "markdown" => $"text/markdown; charset={task.OriginalEncoding}",
            "html" => $"text/html; charset={task.OriginalEncoding}",
            "epub" => "application/epub+zip",
            _ => "application/octet-stream"
        };
        return new ExportedFile(content, contentType, BuildExportFileName(task.FileName, task.FileType));
    }

    private static byte[] ExportEpub(TranslationTask task)
    {
        var templates = JsonSerializer.Deserialize<List<EpubTemplate>>(
                task.ReconstructionTemplate,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidFileTaskRequestException("task_not_exportable", "The EPUB export state is invalid.");
        var templateByPath = templates.ToDictionary(template => template.ResourcePath, StringComparer.Ordinal);
        using var inputStream = new MemoryStream(task.SourceBytes, writable: false);
        using var inputArchive = new ZipArchive(inputStream, ZipArchiveMode.Read);
        using var outputStream = new MemoryStream();
        using (var outputArchive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimetype = inputArchive.GetEntry("mimetype")
                ?? throw new InvalidFileTaskRequestException("task_not_exportable", "The EPUB mimetype entry is missing.");
            CopyEntry(mimetype, outputArchive, CompressionLevel.NoCompression, null);
            foreach (var entry in inputArchive.Entries.Where(entry => entry.FullName != "mimetype"))
            {
                if (templateByPath.TryGetValue(entry.FullName.Replace('\\', '/'), out var template))
                {
                    var rendered = RenderTemplate(template.Template, task.Segments);
                    byte[] originalBytes;
                    using (var original = new MemoryStream())
                    {
                        using var source = entry.Open();
                        source.CopyTo(original);
                        originalBytes = original.ToArray();
                    }

                    CopyEntry(
                        entry,
                        outputArchive,
                        CompressionLevel.Optimal,
                        EncodeText(rendered, originalBytes, template.Encoding));
                }
                else
                {
                    CopyEntry(entry, outputArchive, CompressionLevel.Optimal, null);
                }
            }
        }

        return outputStream.ToArray();
    }

    private static void CopyEntry(
        ZipArchiveEntry source,
        ZipArchive destination,
        CompressionLevel compressionLevel,
        byte[]? replacement)
    {
        var target = destination.CreateEntry(source.FullName, compressionLevel);
        target.ExternalAttributes = source.ExternalAttributes;
        target.LastWriteTime = source.LastWriteTime;
        using var targetStream = target.Open();
        if (replacement is not null)
        {
            targetStream.Write(replacement);
            return;
        }

        using var sourceStream = source.Open();
        sourceStream.CopyTo(targetStream);
    }

    private static string RenderTemplate(
        string template,
        IEnumerable<TranslationSegment> segments)
    {
        var result = template;
        foreach (var segment in segments)
        {
            if (!result.Contains(segment.TemplateToken, StringComparison.Ordinal))
            {
                continue;
            }

            result = result.Replace(
                segment.TemplateToken,
                RestoreMarkup(segment.TargetText!, segment.SourceText, segment.MarkupTableJson),
                StringComparison.Ordinal);
        }

        return result;
    }

    private static string RestoreMarkup(string targetText, string sourceText, string markupTableJson)
    {
        var result = targetText;
        using var document = JsonDocument.Parse(markupTableJson);
        var markupIds = document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt32())
            .ToHashSet();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetInt32();
            if (item.GetProperty("kind").GetString() == "paired")
            {
                var openingToken = $"<x{id}>";
                var closingToken = $"</x{id}>";
                if (CountOccurrences(result, openingToken) != 1 || CountOccurrences(result, closingToken) != 1)
                {
                    throw ExportPlaceholderError("A translated segment has invalid paired placeholders.");
                }

                result = result.Replace(
                    openingToken,
                    item.GetProperty("openingText").GetString(),
                    StringComparison.Ordinal);
                result = result.Replace(
                    closingToken,
                    item.GetProperty("closingText").GetString(),
                    StringComparison.Ordinal);
            }
            else
            {
                var token = $"<x{id}/>";
                if (CountOccurrences(result, token) != 1)
                {
                    throw ExportPlaceholderError("A translated segment has invalid standalone placeholders.");
                }

                result = result.Replace(
                    token,
                    item.GetProperty("originalText").GetString(),
                    StringComparison.Ordinal);
            }
        }

        var allowedLiteralTokens = Regex.Matches(sourceText, @"</?x(?<id>\d+)\s*/?>", RegexOptions.CultureInvariant)
            .Where(match => !markupIds.Contains(int.Parse(
                match.Groups["id"].Value,
                System.Globalization.CultureInfo.InvariantCulture)))
            .GroupBy(match => match.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var actualLiteralTokens = Regex.Matches(result, @"</?x\d+\s*/?>", RegexOptions.CultureInvariant)
            .GroupBy(match => match.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (actualLiteralTokens.Count != allowedLiteralTokens.Count ||
            actualLiteralTokens.Any(pair =>
                !allowedLiteralTokens.TryGetValue(pair.Key, out var expectedCount) || pair.Value != expectedCount))
        {
            throw ExportPlaceholderError("A translated segment contains unknown placeholders.");
        }

        return result;
    }

    private static InvalidFileTaskRequestException ExportPlaceholderError(string message) => new(
        "task_not_exportable",
        message,
        errors: new Dictionary<string, object?>
        {
            ["missingTranslations"] = 0,
            ["placeholderViolations"] = 1
        });

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var position = 0;
        while ((position = value.IndexOf(token, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += token.Length;
        }

        return count;
    }

    private static byte[] EncodeText(string text, byte[] originalBytes, string? encodingName)
    {
        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(encodingName)
                ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(
                    encodingName,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidFileTaskRequestException(
                "target_encoding_unrepresentable",
                "The original text encoding is unavailable.",
                exception);
        }

        ReadOnlySpan<byte> preamble = [];
        if (originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            preamble = Encoding.UTF8.Preamble;
        }
        else if (originalBytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            encoding = new UnicodeEncoding(false, false, true);
            preamble = Encoding.Unicode.Preamble;
        }
        else if (originalBytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            encoding = new UnicodeEncoding(true, false, true);
            preamble = Encoding.BigEndianUnicode.Preamble;
        }

        byte[] encoded;
        try
        {
            encoded = encoding.GetBytes(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidFileTaskRequestException(
                "target_encoding_unrepresentable",
                "The translated text cannot be represented by the original encoding.",
                exception);
        }
        if (preamble.IsEmpty)
        {
            return encoded;
        }

        var result = new byte[preamble.Length + encoded.Length];
        preamble.CopyTo(result.AsSpan());
        encoded.CopyTo(result, preamble.Length);
        return result;
    }

    private static string BuildExportFileName(string fileName, string fileType)
    {
        var safeName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeName);
        if (string.IsNullOrEmpty(extension))
        {
            extension = fileType switch
            {
                "markdown" => ".md",
                "html" => ".html",
                "epub" => ".epub",
                _ => ".txt"
            };
        }

        return $"{Path.GetFileNameWithoutExtension(safeName)}.translated{extension}";
    }

    private static int DecodeCursor(string? cursor, string currentResource, int currentRevision)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            return PaginationCursor.Decode(cursor, currentResource, currentRevision);
        }
        catch (PaginationCursorException exception)
        {
            throw exception.Error == PaginationCursorError.RevisionChanged
                ? new InvalidFileTaskRequestException(
                    "extraction_revision_changed",
                    "The extraction revision changed while paging.",
                    exception)
                : new InvalidFileTaskRequestException(
                    "invalid_cursor",
                    "The pagination cursor is invalid.",
                    exception);
        }
    }

    private static TranslationTask BuildTextTask(
        string sourceKind,
        string fileName,
        string mediaType,
        string? requestedUrl,
        string? finalUrl,
        string fileType,
        byte[] sourceBytes,
        TextExtractionResult extraction)
    {
        var task = new TranslationTask
        {
            Id = Guid.NewGuid(),
            SourceKind = sourceKind,
            FileName = fileName,
            RequestedUrl = requestedUrl,
            FinalUrl = finalUrl,
            MediaType = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType,
            ByteLength = sourceBytes.LongLength,
            FileType = fileType,
            OriginalEncoding = extraction.EncodingName,
            SourceBytes = sourceBytes,
            SegmentationRequested = extraction.RequestedMode,
            SegmentationRecommended = extraction.RecommendedMode,
            SegmentationEffective = extraction.EffectiveMode,
            SegmentationReason = extraction.Reason,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var template = new StringBuilder();
        var segmentOrder = 0;
        var sourceUnitOrder = 0;
        AppendUnits(
            task,
            extraction.Units,
            template,
            ref segmentOrder,
            ref sourceUnitOrder,
            null,
            extraction.EncodingName);

        task.ReconstructionTemplate = template.ToString();
        task.ProtectedBlockCount = task.ProtectedBlocks.Count;
        return task;
    }

    private static TranslationTask BuildEpubTask(
        IFormFile file,
        byte[] sourceBytes,
        EpubExtractionResult extraction)
    {
        var task = new TranslationTask
        {
            Id = Guid.NewGuid(),
            SourceKind = "file",
            FileName = Path.GetFileName(file.FileName),
            MediaType = "application/epub+zip",
            ByteLength = sourceBytes.LongLength,
            FileType = "epub",
            SourceBytes = sourceBytes,
            CreatedAt = DateTimeOffset.UtcNow,
            ChapterCount = extraction.Documents.Count
        };
        var segmentOrder = 0;
        var sourceUnitOrder = 0;
        var templates = new List<EpubTemplate>();
        foreach (var document in extraction.Documents)
        {
            var chapterJson = BuildChapterJson(document);
            var template = new StringBuilder();
            AppendUnits(
                task,
                document.Extraction.Units,
                template,
                ref segmentOrder,
                ref sourceUnitOrder,
                chapterJson,
                document.Extraction.EncodingName);
            templates.Add(new EpubTemplate(
                document.ResourcePath,
                document.Extraction.EncodingName,
                template.ToString()));
        }

        task.ReconstructionTemplate = JsonSerializer.Serialize(
            templates,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        task.ProtectedBlockCount = task.ProtectedBlocks.Count;
        return task;
    }

    private static string BuildChapterJson(EpubDocumentExtraction document) =>
        JsonSerializer.Serialize(
            new ChapterResponse(
                Guid.NewGuid(),
                document.Order,
                document.SpineIndex,
                document.ResourcePath,
                null,
                document.Role),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static void AppendUnits(
        TranslationTask task,
        IReadOnlyList<ExtractedUnit> units,
        StringBuilder template,
        ref int segmentOrder,
        ref int sourceUnitOrder,
        string? chapterJson,
        string encodingName)
    {
        foreach (var unit in units)
        {
            sourceUnitOrder++;
            if (unit.IsSegment)
            {
                segmentOrder++;
                var segmentId = Guid.NewGuid();
                var token = $"{{{{segment:{segmentId:N}}}}}";
                template.Append(token);
                task.Segments.Add(new TranslationSegment
                {
                    Id = segmentId,
                    Order = segmentOrder,
                    SourceUnitOrder = sourceUnitOrder,
                    SourceText = unit.Text,
                    MarkupTableJson = unit.MarkupTableJson,
                    ChapterJson = chapterJson,
                    TemplateToken = token
                });
            }
            else if (unit.Text.Length > 0)
            {
                template.Append(unit.Text);
                task.ProtectedBlocks.Add(new ProtectedBlock
                {
                    Id = Guid.NewGuid(),
                    SourceUnitOrder = sourceUnitOrder,
                    Type = unit.ProtectedType,
                    PreviewText = unit.Text.Length <= 200 ? unit.Text : unit.Text[..200],
                    ByteLength = Encoding.GetEncoding(encodingName).GetByteCount(unit.Text),
                    ContentHash = TextExtraction.Hash(unit.Text, encodingName),
                    ChapterJson = chapterJson
                });
            }
        }
    }

    private static string ResolveFileType(string fileName, string contentType, string? requested)
    {
        if (requested is not null)
        {
            return requested.ToLowerInvariant() switch
            {
                "txt" => "txt",
                "markdown" => "markdown",
                "html" => "html",
                "epub" => "epub",
                _ => throw new InvalidFileTaskRequestException("invalid_file_type", "Unsupported file type.")
            };
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is ".txt" or ".md" or ".markdown" or ".html" or ".htm" or ".epub")
        {
            return extension switch
            {
                ".txt" => "txt",
                ".md" or ".markdown" => "markdown",
                ".html" or ".htm" => "html",
                ".epub" => "epub",
                _ => throw new InvalidOperationException("Unsupported file extension mapping.")
            };
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.ToLowerInvariant() switch
        {
            "text/plain" => "txt",
            "text/markdown" or "text/x-markdown" => "markdown",
            "text/html" or "application/xhtml+xml" => "html",
            "application/epub+zip" => "epub",
            _ => throw new InvalidFileTaskRequestException(
                "unable_to_infer_file_type",
                "The file type could not be inferred.")
        };
    }

    private static FileTaskSummary ToSummary(TranslationTask task) => new(
        task.Id,
        task.Status,
        new FileTaskSource(
            task.SourceKind,
            task.FileName,
            task.RequestedUrl,
            task.FinalUrl,
            task.MediaType,
            task.ByteLength),
        task.FileType,
        task.OriginalEncoding,
        task.ExtractionRevision,
        task.SegmentationEffective is null
            ? null
            : new SegmentationSummary(
                task.SegmentationRequested,
                task.SegmentationRecommended!,
                task.SegmentationEffective,
                task.SegmentationReason!),
        new FileTaskCounts(task.Segments.Count, task.ProtectedBlockCount, task.ChapterCount),
        new FileTaskCapabilities(
            task.FileType is "html" or "epub",
            task.Segments.All(segment => !string.IsNullOrWhiteSpace(segment.TargetText))),
        task.CreatedAt);

    private static SegmentResponse ToResponse(TranslationSegment segment)
    {
        using var markup = JsonDocument.Parse(segment.MarkupTableJson);
        JsonElement? chapter = null;
        if (segment.ChapterJson is not null)
        {
            using var chapterDocument = JsonDocument.Parse(segment.ChapterJson);
            chapter = chapterDocument.RootElement.Clone();
        }

        return new SegmentResponse(
            segment.Id,
            segment.Order,
            segment.SourceText,
            segment.TargetText,
            segment.ConfirmationStatus,
            segment.Version,
            markup.RootElement.Clone(),
            chapter);
    }

    private static ProtectedBlockResponse ToResponse(ProtectedBlock block)
    {
        JsonElement? chapter = null;
        if (block.ChapterJson is not null)
        {
            using var chapterDocument = JsonDocument.Parse(block.ChapterJson);
            chapter = chapterDocument.RootElement.Clone();
        }

        return new ProtectedBlockResponse(
            block.Id,
            block.Type,
            block.PreviewText,
            block.ByteLength,
            block.ContentHash,
            chapter);
    }

    private static SegmentResponse ToResponse(PreviewSegmentData segment)
    {
        using var markup = JsonDocument.Parse(segment.MarkupTableJson);
        JsonElement? chapter = null;
        if (segment.ChapterJson is not null)
        {
            using var chapterDocument = JsonDocument.Parse(segment.ChapterJson);
            chapter = chapterDocument.RootElement.Clone();
        }

        return new SegmentResponse(
            segment.Id,
            segment.Order,
            segment.SourceText,
            null,
            "pending",
            1,
            markup.RootElement.Clone(),
            chapter);
    }

    private ExtractionPreviewData? GetPreview(Guid taskId, Guid previewId)
    {
        if (!previewStore.TryGet(previewId, out var preview) || preview.TaskId != taskId)
        {
            return null;
        }

        if (preview.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidFileTaskRequestException(
                "extraction_preview_expired",
                "The extraction preview expired.");
        }

        return preview;
    }

    private static ExtractionPreviewSummary ToSummary(ExtractionPreviewData preview) => new(
        preview.Id,
        preview.TaskId,
        preview.Selector,
        preview.BaseExtractionRevision,
        new FileTaskCounts(
            preview.Segments.Count,
            preview.ProtectedBlockCount,
            preview.Documents.Count(document => document.ChapterJson is not null)),
        new TranslationLossSummary(
            preview.TranslatedSegmentCount,
            preview.ConfirmedSegmentCount,
            preview.TranslatedSegmentCount > 0 || preview.ConfirmedSegmentCount > 0),
        preview.ExpiresAt);

    private sealed record ChapterResponse(
        Guid Id,
        int Order,
        int? SpineIndex,
        string ResourcePath,
        string? Title,
        string Role);

    private sealed record EpubTemplate(string ResourcePath, string Encoding, string Template);
}

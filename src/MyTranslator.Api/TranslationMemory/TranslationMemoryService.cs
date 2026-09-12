using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.TranslationMemory;

public sealed class TranslationMemoryService(AppDbContext database)
{
    private const double MinimumSourceMatchScore = 0.7;
    private const double WarningSourceMatchScore = 0.85;
    private const double WarningTargetDifference = 0.3;

    /// <summary>由分段确认事务调用；只加入变更跟踪，提交及回滚均由调用方负责。</summary>
    internal async Task AddConfirmedAsync(TranslationTask task, TranslationSegment segment, string targetText,
        string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        using var markup = JsonDocument.Parse(segment.MarkupTableJson);
        var source = segment.SourceText.Trim().Normalize(NormalizationForm.FormC);
        var target = targetText.Trim().Normalize(NormalizationForm.FormC);
        if (source.EnumerateRunes().Count() > 20_000 || target.EnumerateRunes().Count() > 20_000)
            throw new TranslationMemoryRequestException("invalid_tm_target_text", "TM text must not exceed 20000 Unicode scalar values.", 400);
        var prepared = Prepare(new(source, target, sourceLanguage, targetLanguage, markup.RootElement));
        if (database.TranslationMemoryEntries.Local.Any(entry => entry.ContentKey == prepared.ContentKey) ||
            await database.TranslationMemoryEntries.AnyAsync(entry => entry.ContentKey == prepared.ContentKey, cancellationToken)) return;
        var now = DateTimeOffset.UtcNow;
        database.TranslationMemoryEntries.Add(new TranslationMemoryEntry
        {
            Id = Guid.NewGuid(), ContentKey = prepared.ContentKey, SourceText = source, TargetText = target,
            SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage, MarkupTableJson = prepared.MarkupTableJson,
            Origin = "confirmed_segment", OriginTaskId = task.Id, OriginSegmentId = segment.Id,
            OriginExtractionRevision = task.ExtractionRevision, CreatedAt = now, CreatedAtSortKey = SortKey(now)
        });
    }

    public async Task<TranslationMemoryBatchResult> CreateAsync(
        IReadOnlyList<CreateTranslationMemoryEntryRequest> requests,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CreateCoreAsync(requests, cancellationToken);
            }
            catch (Exception exception) when (attempt < 4 && IsRetryableWriteConflict(exception))
            {
                database.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (1 << attempt)), cancellationToken);
            }
        }
    }

    private async Task<TranslationMemoryBatchResult> CreateCoreAsync(
        IReadOnlyList<CreateTranslationMemoryEntryRequest> requests,
        CancellationToken cancellationToken)
    {
        var prepared = requests.Select(Prepare).ToArray();
        var keys = prepared.Select(item => item.ContentKey).Distinct(StringComparer.Ordinal).ToArray();
        var known = await database.TranslationMemoryEntries
            .Where(entry => keys.Contains(entry.ContentKey))
            .ToDictionaryAsync(entry => entry.ContentKey, StringComparer.Ordinal, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var created = 0;
        var items = new List<TranslationMemoryBatchItemResponse>(prepared.Length);

        for (var index = 0; index < prepared.Length; index++)
        {
            var item = prepared[index];
            if (known.TryGetValue(item.ContentKey, out var existing))
            {
                items.Add(new TranslationMemoryBatchItemResponse(index, "duplicate", ToResponse(existing)));
                continue;
            }

            var entry = new TranslationMemoryEntry
            {
                Id = Guid.NewGuid(),
                ContentKey = item.ContentKey,
                SourceText = item.Request.SourceText,
                TargetText = item.Request.TargetText,
                SourceLanguage = item.Request.SourceLanguage,
                TargetLanguage = item.Request.TargetLanguage,
                MarkupTableJson = item.MarkupTableJson,
                Origin = "external",
                CreatedAt = now,
                CreatedAtSortKey = SortKey(now)
            };
            database.TranslationMemoryEntries.Add(entry);
            known.Add(entry.ContentKey, entry);
            created++;
            items.Add(new TranslationMemoryBatchItemResponse(index, "created", ToResponse(entry)));
        }

        await database.SaveChangesAsync(cancellationToken);
        return new TranslationMemoryBatchResult(
            new TranslationMemoryBatchResponse(
                new TranslationMemoryBatchSummary(prepared.Length, created, prepared.Length - created),
                items),
            created > 0);
    }

    public async Task<TranslationMemoryPage> ListAsync(
        string sourceLanguage,
        string targetLanguage,
        string? query,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            throw new TranslationMemoryRequestException(
                "invalid_pagination",
                "The page size must be between 1 and 200.",
                StatusCodes.Status400BadRequest);
        }

        if (sourceLanguage == targetLanguage)
        {
            throw new TranslationMemoryRequestException(
                "invalid_language_tag",
                "Source and target languages must differ.",
                StatusCodes.Status400BadRequest);
        }

        var position = cursor is null
            ? null
            : TranslationMemoryCursor.Decode(cursor, query, sourceLanguage, targetLanguage);
        var entryQuery = database.TranslationMemoryEntries.AsNoTracking()
            .Where(entry =>
                entry.SourceLanguage == sourceLanguage &&
                entry.TargetLanguage == targetLanguage);
        if (query is null)
        {
            if (position is not null)
            {
                entryQuery = entryQuery.Where(entry =>
                    string.Compare(
                        EF.Functions.Collate(entry.CreatedAtSortKey, "BINARY"),
                        position.CreatedAtSortKey) < 0 ||
                    entry.CreatedAtSortKey == position.CreatedAtSortKey &&
                    entry.Id.CompareTo(position.EntryId) > 0);
            }

            var rows = await entryQuery
                .OrderByDescending(entry => entry.CreatedAtSortKey)
                .ThenBy(entry => entry.Id)
                .Take(limit + 1)
                .ToListAsync(cancellationToken);
            return CreatePage(
                rows.Select(entry => new ScoredEntry(entry, null)).ToList(),
                query,
                sourceLanguage,
                targetLanguage,
                limit);
        }

        var entries = await entryQuery.ToListAsync(cancellationToken);
        var scored = OrderScoredEntries(entries.Select(entry => new ScoredEntry(
                entry,
                TranslationMemoryText.Similarity(
                    entry.SourceText,
                    query,
                    entry.MarkupTableJson,
                    "[]",
                    MinimumSourceMatchScore)))
            .Where(item => item.SourceMatchScore >= MinimumSourceMatchScore));
        var afterCursor = position is null
            ? scored
            : scored.Where(item => IsAfter(item, position));
        return CreatePage(
            afterCursor.Take(limit + 1).ToList(),
            query,
            sourceLanguage,
            targetLanguage,
            limit);
    }

    public async Task<TranslationMemoryEntryResponse> GetAsync(
        Guid entryId,
        CancellationToken cancellationToken)
    {
        var entry = await database.TranslationMemoryEntries.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        return entry is null ? throw EntryNotFound() : ToResponse(entry);
    }

    public async Task DeleteAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await database.TranslationMemoryEntries
            .SingleOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        if (entry is null)
        {
            throw EntryNotFound();
        }

        database.TranslationMemoryEntries.Remove(entry);
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 通用文本对比（§6.2）。请求正文的标记表按写入路径校验（§2.2），不合法即拒绝。
    /// </summary>
    public Task<TranslationMemoryComparisonResponse> CompareAsync(
        TranslationMemoryComparisonRequest request,
        CancellationToken cancellationToken) => CompareCoreAsync(
        request.SourceText,
        request.TargetText,
        request.SourceLanguage,
        request.TargetLanguage,
        request.Limit,
        TranslationMemoryMarkup.ValidateAndCanonicalize(request.MarkupTable, request.SourceText, request.TargetText),
        binding: null,
        cancellationToken);

    /// <summary>
    /// 对比实现。标记表不在此处校验：通用对比传入写入路径校验结果；任务分段对比传入服务端已存标记表
    /// ——§6.1 是对已提交数据的只读快照，历史数据的形状缺陷（如 paired 缺 <c>closingText</c>）
    /// 不应让只读对比返回调用方无法修正的 4xx。
    /// </summary>
    private async Task<TranslationMemoryComparisonResponse> CompareCoreAsync(
        string sourceText,
        string? targetText,
        string sourceLanguage,
        string targetLanguage,
        int limit,
        string markupTableJson,
        SegmentBinding? binding,
        CancellationToken cancellationToken)
    {
        var entries = await database.TranslationMemoryEntries.AsNoTracking()
            .Where(entry =>
                entry.SourceLanguage == sourceLanguage &&
                entry.TargetLanguage == targetLanguage)
            .ToListAsync(cancellationToken);
        var matches = OrderScoredEntries(entries.Select(entry => new ScoredEntry(
                entry,
                TranslationMemoryText.Similarity(
                    sourceText,
                    entry.SourceText,
                    markupTableJson,
                    entry.MarkupTableJson,
                    MinimumSourceMatchScore)))
            .Where(item => item.SourceMatchScore >= MinimumSourceMatchScore))
            .Take(limit)
            .Select(item => ToMatchResponse(item, targetText, markupTableJson))
            .ToArray();
        var reference = targetText is null
            ? null
            : matches.FirstOrDefault(item => item.SourceMatchScore >= WarningSourceMatchScore);
        var hasWarning = reference?.TargetDifference > WarningTargetDifference;

        return new TranslationMemoryComparisonResponse(
            binding?.TaskId,
            binding?.SegmentId,
            binding?.ExtractionRevision,
            binding?.SegmentVersion,
            sourceLanguage,
            targetLanguage,
            new TranslationMemoryThresholds(
                MinimumSourceMatchScore,
                WarningSourceMatchScore,
                WarningTargetDifference),
            matches,
            new TranslationMemoryDifferenceWarning(
                hasWarning,
                reference?.EntryId,
                reference?.SourceMatchScore,
                reference?.TargetDifference,
                hasWarning ? "target_difference_exceeded" : null),
            DateTimeOffset.UtcNow);
    }

    public async Task<TranslationMemoryComparisonResponse> CompareSegmentAsync(
        Guid taskId,
        Guid segmentId,
        int extractionRevision,
        int segmentVersion,
        string sourceLanguage,
        string targetLanguage,
        int limit,
        CancellationToken cancellationToken)
    {
        if (sourceLanguage == targetLanguage)
        {
            throw new TranslationMemoryRequestException(
                "invalid_language_tag",
                "Source and target languages must differ.",
                StatusCodes.Status400BadRequest);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var taskRevision = await database.TranslationTasks.AsNoTracking()
            .Where(task => task.Id == taskId)
            .Select(task => (int?)task.ExtractionRevision)
            .SingleOrDefaultAsync(cancellationToken);
        if (taskRevision is null)
        {
            throw new TranslationMemoryRequestException(
                "task_not_found",
                "Task not found.",
                StatusCodes.Status404NotFound);
        }

        if (taskRevision.Value != extractionRevision)
        {
            throw new TranslationMemoryRequestException(
                "extraction_revision_changed",
                "The extraction revision changed since it was read.",
                StatusCodes.Status409Conflict,
                new Dictionary<string, object?>
                {
                    ["requestedRevision"] = extractionRevision,
                    ["currentRevision"] = taskRevision.Value
                });
        }

        var segment = await database.TranslationSegments.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == segmentId && item.TaskId == taskId,
                cancellationToken);
        if (segment is null)
        {
            throw new TranslationMemoryRequestException(
                "segment_not_found",
                "Segment not found.",
                StatusCodes.Status404NotFound);
        }

        if (segment.Version != segmentVersion)
        {
            throw new TranslationMemoryRequestException(
                "segment_version_conflict",
                "The segment has changed since it was read.",
                StatusCodes.Status409Conflict,
                new Dictionary<string, object?>
                {
                    ["requestedVersion"] = segmentVersion,
                    ["currentVersion"] = segment.Version
                });
        }

        var response = await CompareCoreAsync(
            segment.SourceText,
            segment.TargetText,
            sourceLanguage,
            targetLanguage,
            limit,
            segment.MarkupTableJson,
            new SegmentBinding(taskId, segmentId, extractionRevision, segmentVersion),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    internal static TranslationMemoryEntryResponse ToResponse(TranslationMemoryEntry entry)
    {
        using var markup = JsonDocument.Parse(entry.MarkupTableJson);
        return new TranslationMemoryEntryResponse(
            entry.Id,
            entry.SourceText,
            entry.TargetText,
            entry.SourceLanguage,
            entry.TargetLanguage,
            markup.RootElement.Clone(),
            entry.Origin,
            entry.OriginTaskId,
            entry.OriginSegmentId,
            entry.OriginExtractionRevision,
            entry.CreatedAt);
    }

    private static PreparedEntry Prepare(CreateTranslationMemoryEntryRequest request)
    {
        var markupTableJson = TranslationMemoryMarkup.ValidateAndCanonicalize(
            request.MarkupTable,
            request.SourceText,
            request.TargetText);
        var canonical = string.Join(
            '\u001f',
            request.SourceLanguage,
            request.TargetLanguage,
            TranslationMemoryText.NormalizeForSimilarity(request.SourceText, markupTableJson),
            TranslationMemoryText.NormalizeForSimilarity(request.TargetText, markupTableJson),
            markupTableJson);
        var contentKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new PreparedEntry(request, markupTableJson, contentKey);
    }

    private static TranslationMemoryPage CreatePage(
        List<ScoredEntry> rows,
        string? query,
        string sourceLanguage,
        string targetLanguage,
        int limit)
    {
        var hasNext = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        var items = rows.Select(ToListResponse).ToArray();
        var nextCursor = hasNext
            ? TranslationMemoryCursor.Encode(
                query,
                sourceLanguage,
                targetLanguage,
                rows[^1].SourceMatchScore,
                rows[^1].Entry.CreatedAtSortKey,
                rows[^1].Entry.Id)
            : null;
        return new TranslationMemoryPage(items, nextCursor);
    }

    private static TranslationMemoryListItemResponse ToListResponse(ScoredEntry item)
    {
        using var markup = JsonDocument.Parse(item.Entry.MarkupTableJson);
        return new TranslationMemoryListItemResponse(
            item.Entry.Id,
            item.Entry.SourceText,
            item.Entry.TargetText,
            item.Entry.SourceLanguage,
            item.Entry.TargetLanguage,
            markup.RootElement.Clone(),
            item.Entry.Origin,
            item.Entry.OriginTaskId,
            item.Entry.OriginSegmentId,
            item.Entry.OriginExtractionRevision,
            item.Entry.CreatedAt,
            item.SourceMatchScore);
    }

    private static TranslationMemoryMatchResponse ToMatchResponse(
        ScoredEntry item,
        string? targetText,
        string targetMarkupTableJson)
    {
        using var markup = JsonDocument.Parse(item.Entry.MarkupTableJson);
        var targetSimilarity = targetText is null
            ? (double?)null
            : TranslationMemoryText.Similarity(
                targetText,
                item.Entry.TargetText,
                targetMarkupTableJson,
                item.Entry.MarkupTableJson);
        return new TranslationMemoryMatchResponse(
            item.Entry.Id,
            item.Entry.SourceText,
            item.Entry.TargetText,
            markup.RootElement.Clone(),
            item.SourceMatchScore!.Value,
            targetSimilarity,
            targetSimilarity is null
                ? null
                : Math.Round(1.0 - targetSimilarity.Value, 4, MidpointRounding.AwayFromZero),
            item.Entry.CreatedAt);
    }

    private static bool IsAfter(ScoredEntry item, TranslationMemoryCursorPosition position)
    {
        if (item.SourceMatchScore != position.SourceMatchScore)
        {
            return item.SourceMatchScore < position.SourceMatchScore;
        }

        return string.CompareOrdinal(item.Entry.CreatedAtSortKey, position.CreatedAtSortKey) < 0 ||
               item.Entry.CreatedAtSortKey == position.CreatedAtSortKey &&
               item.Entry.Id.CompareTo(position.EntryId) > 0;
    }

    private static IOrderedEnumerable<ScoredEntry> OrderScoredEntries(IEnumerable<ScoredEntry> entries) =>
        entries.OrderByDescending(item => item.SourceMatchScore)
            .ThenByDescending(item => item.Entry.CreatedAtSortKey, StringComparer.Ordinal)
            .ThenBy(item => item.Entry.Id);

    private static string SortKey(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    private static TranslationMemoryRequestException EntryNotFound() => new(
        "tm_entry_not_found",
        "Translation memory entry not found.",
        StatusCodes.Status404NotFound);

    private static bool IsRetryableWriteConflict(Exception exception) => exception switch
    {
        DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 19 } } => true,
        DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 } } => true,
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
        _ => false
    };

    private sealed record PreparedEntry(
        CreateTranslationMemoryEntryRequest Request,
        string MarkupTableJson,
        string ContentKey);

    private sealed record ScoredEntry(TranslationMemoryEntry Entry, double? SourceMatchScore);

    /// <summary>任务分段对比的绑定字段；通用文本对比为 <c>null</c>（响应中对应字段回显为 null，§6.3）。</summary>
    private sealed record SegmentBinding(
        Guid TaskId,
        Guid SegmentId,
        int ExtractionRevision,
        int SegmentVersion);
}

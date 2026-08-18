using System.Data;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Terms;

public sealed class TermAlignmentService(AppDbContext database)
{
    public async Task<TermAlignmentResponse> CheckAsync(
        Guid taskId,
        TermAlignmentRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var task = await database.TranslationTasks.AsNoTracking()
            .Where(item => item.Id == taskId)
            .Select(item => new { item.Id, item.ExtractionRevision })
            .SingleOrDefaultAsync(cancellationToken);
        if (task is null)
        {
            throw new TermRequestException(
                "task_not_found",
                "Task not found.",
                StatusCodes.Status404NotFound);
        }

        if (task.ExtractionRevision != request.ExtractionRevision)
        {
            throw new TermRequestException(
                "extraction_revision_changed",
                "The task extraction revision has changed.",
                StatusCodes.Status409Conflict,
                new Dictionary<string, object?>
                {
                    ["requestedRevision"] = request.ExtractionRevision,
                    ["currentRevision"] = task.ExtractionRevision
                });
        }

        var terms = await database.Terms.AsNoTracking()
            .Where(term => term.SourceLanguage == request.SourceLanguage &&
                           term.TargetLanguage == request.TargetLanguage)
            .ToListAsync(cancellationToken);
        var segments = await database.TranslationSegments.AsNoTracking()
            .Where(segment => segment.TaskId == taskId)
            .OrderBy(segment => segment.Order)
            .ToListAsync(cancellationToken);
        var invalidSegments = segments
            .Where(segment => segment.TargetText is not null && string.IsNullOrWhiteSpace(segment.TargetText))
            .ToArray();
        if (invalidSegments.Length > 0)
        {
            var first = invalidSegments[0];
            throw new TermRequestException(
                "invalid_segment_state",
                "A translated segment must contain at least one non-whitespace character.",
                StatusCodes.Status422UnprocessableEntity,
                new Dictionary<string, object?>
                {
                    ["invalidSegmentCount"] = invalidSegments.Length,
                    ["firstInvalidSegmentId"] = first.Id,
                    ["firstInvalidSegmentOrder"] = first.Order
                });
        }

        var checkedSegments = segments.Where(segment => segment.TargetText is not null).ToArray();
        var matchedSegments = 0;
        var alignedSegments = 0;
        var items = new List<UnalignedSegmentResponse>();
        foreach (var segment in checkedSegments)
        {
            var alignment = TermAlignmentMatcher.Match(
                terms,
                segment.SourceText,
                segment.TargetText!,
                segment.MarkupTableJson);
            if (!alignment.MatchedAny)
            {
                continue;
            }

            matchedSegments++;
            if (alignment.Misalignments.Count == 0)
            {
                alignedSegments++;
                continue;
            }

            items.Add(new UnalignedSegmentResponse(
                segment.Id,
                segment.Order,
                segment.Version,
                alignment.Misalignments
                    .OrderBy(item => item.SourceTerm, StringComparer.Ordinal)
                    .ThenBy(item => item.TermId)
                    .ToArray()));
        }

        var response = new TermAlignmentResponse(
            taskId,
            task.ExtractionRevision,
            request.SourceLanguage,
            request.TargetLanguage,
            new TermAlignmentSummary(
                segments.Count,
                checkedSegments.Length,
                segments.Count - checkedSegments.Length,
                terms.Count,
                matchedSegments,
                alignedSegments,
                items.Count),
            items,
            DateTimeOffset.UtcNow);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }
}

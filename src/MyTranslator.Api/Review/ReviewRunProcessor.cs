using System.ClientModel;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MyTranslator.Api.Data;
using MyTranslator.Api.Translation;
using MyTranslator.Api.Terms;

namespace MyTranslator.Api.Review;

public sealed class ReviewRunProcessor(
    AppDbContext database,
    IAiChatClientFactory chatClientFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ProcessAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await database.ReviewRuns
            .SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken);
        if (run is null || run.Status != ReviewRunStatus.Queued)
        {
            return;
        }

        run.Status = ReviewRunStatus.Processing;
        run.StartedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        while (true)
        {
            var configuration = await LoadConfigurationAsync(run, cancellationToken);
            var segments = await LoadNextBatchAsync(
                run.Id,
                configuration?.Provider.BatchSize ?? AiProviderRuntimeSettings.DefaultBatchSize,
                cancellationToken);
            if (segments.Count == 0)
            {
                break;
            }

            if (configuration is null)
            {
                await RecordFailuresAsync(
                    run.Id,
                    segments,
                    segments.ToDictionary(
                        segment => segment.SegmentId,
                        _ => new FailureCause("llm_model_unavailable", false)),
                    0,
                    cancellationToken);
                continue;
            }

            await ProcessBatchAsync(run, configuration, segments, cancellationToken);
        }

        await FinalizeAsync(runId, cancellationToken);
    }

    private async Task<AiExecutionConfiguration?> LoadConfigurationAsync(
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var provider = await database.Providers
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == run.ProviderId, cancellationToken);
        var model = await database.Models
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == run.ModelId && item.ProviderId == run.ProviderId,
                cancellationToken);
        return provider is null || model is null || !AiConfigurationAvailability.IsAvailable(provider, model)
            ? null
            : new AiExecutionConfiguration(provider, model);
    }

    private async Task<IReadOnlyList<ReviewRunSegment>> LoadNextBatchAsync(
        Guid runId,
        int batchSize,
        CancellationToken cancellationToken) =>
        await database.ReviewRunSegments
            .AsNoTracking()
            .Where(segment => segment.RunId == runId && !segment.Processed)
            .OrderBy(segment => segment.SegmentOrder)
            .Take(AiProviderRuntimeSettings.ClampBatchSize(batchSize))
            .ToListAsync(cancellationToken);

    private async Task ProcessBatchAsync(
        ReviewRun run,
        AiExecutionConfiguration configuration,
        IReadOnlyList<ReviewRunSegment> batch,
        CancellationToken cancellationToken)
    {
        var pending = batch.ToDictionary(segment => segment.SegmentId);
        var lastFailures = pending.Keys.ToDictionary(
            id => id,
            _ => new FailureCause("llm_response_invalid", true));
        var maxAttempts = AiProviderRuntimeSettings.ClampMaxAttempts(configuration.Provider.MaxAttempts);
        var termSnapshot = JsonSerializer.Deserialize<ReviewTermSnapshot[]>(run.TermSnapshotJson, JsonOptions) ?? [];

        IChatClient chatClient;
        try
        {
            chatClient = chatClientFactory.Create(configuration.Provider, configuration.Model);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailuresAsync(
                run.Id,
                pending.Values,
                pending.Keys.ToDictionary(
                    id => id,
                    _ => new FailureCause("llm_model_unavailable", false)),
                0,
                cancellationToken);
            return;
        }

        using (chatClient)
        {
            for (var attempt = 1; attempt <= maxAttempts && pending.Count > 0; attempt++)
            {
                IReadOnlyList<ReviewOutput> outputs;
                try
                {
                    outputs = await RequestReviewsAsync(
                        chatClient,
                        run.SourceLanguage,
                        run.TargetLanguage,
                        pending.Values.OrderBy(segment => segment.SegmentOrder).ToArray(),
                        termSnapshot,
                        configuration.Provider.RequestTimeout,
                        cancellationToken);
                }
                catch (ReviewExecutionException exception)
                {
                    foreach (var segmentId in pending.Keys)
                    {
                        lastFailures[segmentId] = new FailureCause(exception.Code, exception.Retryable);
                    }

                    if (!exception.Retryable || attempt == maxAttempts)
                    {
                        await RecordFailuresAsync(run.Id, pending.Values, lastFailures, attempt, cancellationToken);
                        return;
                    }

                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                var outputLookup = outputs
                    .GroupBy(output => output.SegmentId)
                    .ToDictionary(group => group.Key, group => group.ToArray());
                if (outputLookup.Count != pending.Count ||
                    outputLookup.Any(pair => pair.Value.Length != 1 || !pending.ContainsKey(pair.Key)))
                {
                    if (attempt == maxAttempts)
                    {
                        await RecordFailuresAsync(run.Id, pending.Values, lastFailures, attempt, cancellationToken);
                        return;
                    }

                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                var successful = new Dictionary<Guid, IReadOnlyList<ReviewCommentValue>>();
                foreach (var segment in pending.Values)
                {
                    var output = outputLookup[segment.SegmentId][0];
                    if (output.StructurallyValid &&
                        TryNormalizeComments(output.Comments, out var comments))
                    {
                        successful[segment.SegmentId] = comments;
                    }
                    else
                    {
                        lastFailures[segment.SegmentId] = new FailureCause("llm_response_invalid", true);
                    }
                }

                if (successful.Count > 0)
                {
                    await CommitReviewsAsync(run.Id, successful, cancellationToken);
                    foreach (var segmentId in successful.Keys)
                    {
                        pending.Remove(segmentId);
                    }
                }

                if (pending.Count == 0)
                {
                    return;
                }

                if (attempt == maxAttempts)
                {
                    await RecordFailuresAsync(run.Id, pending.Values, lastFailures, attempt, cancellationToken);
                    return;
                }

                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
        }
    }

    private static async Task<IReadOnlyList<ReviewOutput>> RequestReviewsAsync(
        IChatClient chatClient,
        string? sourceLanguage,
        string targetLanguage,
        IReadOnlyList<ReviewRunSegment> segments,
        IReadOnlyList<ReviewTermSnapshot> terms,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new
        {
            sourceLanguage,
            targetLanguage,
            segments = segments.Select(segment => new
            {
                segmentId = segment.SegmentId,
                sourceText = segment.SourceText,
                targetText = segment.TargetText,
                terms = FindTerms(terms, segment.SourceText, segment.MarkupTableJson).Select(term => new
                {
                    sourceTerm = term.SourceTerm,
                    targetTerm = term.TargetTerm,
                    caseSensitive = term.CaseSensitive
                })
            })
        }, JsonOptions);
        var messages = new[]
        {
            new ChatMessage(
                ChatRole.System,
                "Review every translation for fidelity (mistranslation, omission, and addition), required terminology, naturalness, style, and consistency. A null sourceLanguage means detect the source language automatically. Return JSON as {\"reviews\":[{\"segmentId\":\"uuid\",\"comments\":[{\"severity\":\"high|medium|low\",\"issue\":\"...\",\"suggestion\":\"... or null\"}]}]}. Return every requested segment ID exactly once and no additional IDs. Return zero to twenty comments per segment; never exceed 20, keeping higher-severity non-duplicate issues first. Treat <xN>, </xN>, and <xN/> tokens as transparent placeholders: do not translate, rewrite, delete, add, or move them, and report any placeholder problem as a review comment."),
            new ChatMessage(ChatRole.User, input)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);

        try
        {
            var response = await chatClient.GetResponseAsync(
                messages,
                new ChatOptions
                {
                    Temperature = 0,
                    ResponseFormat = ChatResponseFormat.Json
                },
                timeout.Token);
            if (string.IsNullOrWhiteSpace(response.Text))
            {
                throw InvalidResponse();
            }

            using var document = JsonDocument.Parse(StripCodeFence(response.Text));
            return document.RootElement
                .GetProperty("reviews")
                .EnumerateArray()
                .Select(ParseOutput)
                .ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReviewExecutionException(
                "llm_provider_timeout",
                true,
                "The AI provider request timed out.",
                exception);
        }
        catch (ClientResultException exception)
        {
            throw MapStatus(exception.Status, HasUnsupportedLanguageCode(exception), exception);
        }
        catch (HttpRequestException exception)
        {
            throw exception.StatusCode is { } status
                ? MapStatus((int)status, false, exception)
                : new ReviewExecutionException(
                    "llm_provider_unavailable",
                    true,
                    "The AI provider is unavailable.",
                    exception);
        }
        catch (Exception exception) when (exception is
            JsonException or
            KeyNotFoundException or
            InvalidOperationException or
            FormatException or
            IndexOutOfRangeException or
            ArgumentOutOfRangeException)
        {
            throw InvalidResponse(exception);
        }
    }

    private static ReviewOutput ParseOutput(JsonElement item)
    {
        var segmentId = item.GetProperty("segmentId").GetGuid();
        if (!item.TryGetProperty("comments", out var commentsProperty) ||
            commentsProperty.ValueKind != JsonValueKind.Array)
        {
            return new ReviewOutput(segmentId, [], false);
        }

        var comments = new List<ReviewCommentInput>();
        foreach (var comment in commentsProperty.EnumerateArray())
        {
            if (comment.ValueKind != JsonValueKind.Object || !TryParseComment(comment, out var parsed))
            {
                return new ReviewOutput(segmentId, [], false);
            }

            comments.Add(parsed);
        }

        return new ReviewOutput(segmentId, comments, true);
    }

    private static bool TryParseComment(JsonElement item, out ReviewCommentInput comment)
    {
        var severity = item.TryGetProperty("severity", out var severityProperty) &&
                       severityProperty.ValueKind == JsonValueKind.String
            ? severityProperty.GetString()
            : null;
        var issue = item.TryGetProperty("issue", out var issueProperty) &&
                    issueProperty.ValueKind == JsonValueKind.String
            ? issueProperty.GetString()
            : null;
        string? suggestion = null;
        if (item.TryGetProperty("suggestion", out var suggestionProperty))
        {
            if (suggestionProperty.ValueKind == JsonValueKind.String)
            {
                suggestion = suggestionProperty.GetString();
            }
            else if (suggestionProperty.ValueKind != JsonValueKind.Null)
            {
                comment = new ReviewCommentInput(severity, issue, null);
                return false;
            }
        }

        comment = new ReviewCommentInput(severity, issue, suggestion);
        return true;
    }

    private static bool TryNormalizeComments(
        IReadOnlyList<ReviewCommentInput> input,
        out IReadOnlyList<ReviewCommentValue> comments)
    {
        if (input.Count > 20)
        {
            comments = [];
            return false;
        }

        var normalized = new List<ReviewCommentValue>(input.Count);
        foreach (var comment in input)
        {
            var issue = comment.Issue?.Trim();
            var suggestion = comment.Suggestion?.Trim();
            if (suggestion?.Length == 0)
            {
                suggestion = null;
            }
            if (string.IsNullOrEmpty(issue) || CountScalars(issue) > 2000 ||
                suggestion is not null && CountScalars(suggestion) > 4000)
            {
                comments = [];
                return false;
            }

            var severity = comment.Severity?.Trim().ToLowerInvariant() switch
            {
                "high" => ReviewSeverity.High,
                "low" => ReviewSeverity.Low,
                _ => ReviewSeverity.Medium
            };
            normalized.Add(new ReviewCommentValue(severity, issue, suggestion));
        }

        comments = normalized
            .OrderBy(comment => comment.Severity switch
            {
                ReviewSeverity.High => 0,
                ReviewSeverity.Medium => 1,
                _ => 2
            })
            .ToArray();
        return true;
    }

    private async Task CommitReviewsAsync(
        Guid runId,
        IReadOnlyDictionary<Guid, IReadOnlyList<ReviewCommentValue>> reviews,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var segmentIds = reviews.Keys.ToArray();
        await database.ReviewComments
            .Where(comment => segmentIds.Contains(comment.SegmentId))
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var (segmentId, comments) in reviews)
        {
            database.ReviewComments.AddRange(comments.Select((comment, position) => new ReviewComment
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                SegmentId = segmentId,
                Position = position,
                Severity = comment.Severity,
                Issue = comment.Issue,
                Suggestion = comment.Suggestion
            }));
        }

        var runSegments = await database.ReviewRunSegments
            .Where(segment => segment.RunId == runId && segmentIds.Contains(segment.SegmentId))
            .ToListAsync(cancellationToken);
        foreach (var segment in runSegments)
        {
            segment.Processed = true;
            segment.Succeeded = true;
        }

        var run = await database.ReviewRuns.SingleAsync(entity => entity.Id == runId, cancellationToken);
        run.SucceededSegments += runSegments.Count;
        run.ProcessedSegments += runSegments.Count;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    private async Task RecordFailuresAsync(
        Guid runId,
        IEnumerable<ReviewRunSegment> segments,
        IReadOnlyDictionary<Guid, FailureCause> causes,
        int attempts,
        CancellationToken cancellationToken)
    {
        var segmentArray = segments.ToArray();
        var segmentIds = segmentArray.Select(segment => segment.SegmentId).ToArray();
        database.ReviewRunFailures.AddRange(segmentArray.Select(segment =>
        {
            var cause = causes[segment.SegmentId];
            return new ReviewRunFailure
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                SegmentId = segment.SegmentId,
                SegmentOrder = segment.SegmentOrder,
                Code = cause.Code,
                Retryable = cause.Retryable,
                Attempts = attempts
            };
        }));
        var runSegments = await database.ReviewRunSegments
            .Where(segment => segment.RunId == runId && segmentIds.Contains(segment.SegmentId))
            .ToListAsync(cancellationToken);
        foreach (var segment in runSegments)
        {
            segment.Processed = true;
        }

        var run = await database.ReviewRuns.SingleAsync(entity => entity.Id == runId, cancellationToken);
        run.FailedSegments += runSegments.Count;
        run.ProcessedSegments += runSegments.Count;
        await database.SaveChangesAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    private async Task FinalizeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await database.ReviewRuns
            .Include(entity => entity.Failures)
            .SingleAsync(entity => entity.Id == runId, cancellationToken);
        if (run.FailedSegments == 0)
        {
            run.Status = ReviewRunStatus.Completed;
        }
        else
        {
            run.Status = run.SucceededSegments > 0
                ? ReviewRunStatus.PartialFailed
                : ReviewRunStatus.Failed;
            var codes = run.Failures.Select(failure => failure.Code).Distinct(StringComparer.Ordinal).ToArray();
            run.FailureCode = codes.Length == 1 ? codes[0] : "segment_review_failed";
            run.FailureRetryable = codes.Length > 1 || run.Failures.Any(failure => failure.Retryable);
        }

        run.ProcessedSegments = run.SelectedSegments;
        run.ActiveTaskLockId = null;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<ReviewTermSnapshot> FindTerms(
        IReadOnlyList<ReviewTermSnapshot> terms,
        string sourceText,
        string markupTableJson) => terms
        .Where(term => TermAlignmentMatcher.CountSourceOccurrences(
            sourceText,
            markupTableJson,
            term.SourceTerm,
            term.CaseSensitive) > 0)
        .ToArray();

    private static int CountScalars(string value) => value.EnumerateRunes().Count();

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var exponentialDelay = 50 * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * 25;
        return Task.Delay(TimeSpan.FromMilliseconds(exponentialDelay + jitter), cancellationToken);
    }

    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? trimmed[(firstLineEnd + 1)..lastFence].Trim()
            : trimmed;
    }

    private static bool HasUnsupportedLanguageCode(ClientResultException exception)
    {
        try
        {
            var content = exception.GetRawResponse()?.Content;
            if (content is null)
            {
                return false;
            }

            using var document = JsonDocument.Parse(content.ToString());
            var root = document.RootElement;
            var code = root.TryGetProperty("error", out var error) &&
                       error.ValueKind == JsonValueKind.Object &&
                       error.TryGetProperty("code", out var nestedCode)
                ? nestedCode.GetString()
                : root.TryGetProperty("code", out var topLevelCode)
                    ? topLevelCode.GetString()
                    : null;
            return string.Equals(code, "unsupported_language_pair", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception parseException) when (parseException is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static ReviewExecutionException MapStatus(
        int status,
        bool unsupportedLanguagePair,
        Exception exception)
    {
        if (unsupportedLanguagePair && status is 400 or 422)
        {
            return new ReviewExecutionException(
                "unsupported_language_pair",
                false,
                "The AI provider does not support the requested language pair.",
                exception);
        }

        return status switch
        {
            0 => new("llm_provider_unavailable", true, "The AI provider is unavailable.", exception),
            401 or 403 => new("llm_authentication_failed", false, "The AI provider rejected the configured credentials.", exception),
            404 => new("llm_model_unavailable", false, "The selected AI model is unavailable.", exception),
            408 or 504 => new("llm_provider_timeout", true, "The AI provider timed out.", exception),
            429 or >= 500 => new("llm_provider_unavailable", true, "The AI provider is unavailable.", exception),
            _ => new("llm_response_invalid", true, "The AI provider rejected the review request.", exception)
        };
    }

    private static ReviewExecutionException InvalidResponse(Exception? exception = null) => new(
        "llm_response_invalid",
        true,
        "The AI provider returned an invalid structured response.",
        exception);

    private sealed record AiExecutionConfiguration(AiProvider Provider, AiModel Model);
    private sealed record ReviewOutput(
        Guid SegmentId,
        IReadOnlyList<ReviewCommentInput> Comments,
        bool StructurallyValid);
    private sealed record ReviewCommentInput(string? Severity, string? Issue, string? Suggestion);
    private sealed record ReviewCommentValue(ReviewSeverity Severity, string Issue, string? Suggestion);
    private sealed record FailureCause(string Code, bool Retryable);
}

internal sealed record ReviewTermSnapshot(string SourceTerm, string TargetTerm, bool CaseSensitive);

internal sealed class ReviewExecutionException(
    string code,
    bool retryable,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

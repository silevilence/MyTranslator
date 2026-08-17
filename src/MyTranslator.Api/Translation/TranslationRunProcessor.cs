using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MyTranslator.Api.Data;
using MyTranslator.Api.Rules;
using System.ClientModel;

namespace MyTranslator.Api.Translation;

public sealed class TranslationRunProcessor(
    AppDbContext database,
    IAiChatClientFactory chatClientFactory,
    IEnumerable<ITranslationRule> rules)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ProcessAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await database.TranslationRuns
            .SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken);
        if (run is null || run.Status != TranslationRunStatus.Queued)
        {
            return;
        }

        run.Status = TranslationRunStatus.Processing;
        run.StartedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        while (true)
        {
            var configuration = await LoadConfigurationAsync(run, cancellationToken);
            var segments = await LoadNextBatchAsync(
                run,
                configuration?.Provider.BatchSize ?? 20,
                cancellationToken);
            if (segments.Count == 0)
            {
                break;
            }

            if (configuration is null)
            {
                var cause = segments.ToDictionary(
                    segment => segment.Id,
                    _ => new FailureCause("llm_model_unavailable", false));
                await RecordFailuresAsync(run.Id, segments, cause, 0, cancellationToken);
                continue;
            }

            await ProcessBatchAsync(run, configuration, segments, cancellationToken);
        }

        await FinalizeAsync(runId, cancellationToken);
    }

    private async Task<AiExecutionConfiguration?> LoadConfigurationAsync(
        TranslationRun run,
        CancellationToken cancellationToken)
    {
        if (run.ProviderId is null || run.ModelId is null)
        {
            return null;
        }

        var provider = await database.Providers
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == run.ProviderId, cancellationToken);
        var model = await database.Models
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == run.ModelId && item.ProviderId == run.ProviderId,
                cancellationToken);
        if (provider is null || model is null || !provider.Enabled ||
            string.IsNullOrWhiteSpace(provider.BaseUrl) ||
            (provider.Kind == "openai" && string.IsNullOrWhiteSpace(provider.ApiKey)) ||
            string.IsNullOrWhiteSpace(model.ModelId))
        {
            return null;
        }

        return new AiExecutionConfiguration(provider, model);
    }

    private async Task<IReadOnlyList<SegmentWork>> LoadNextBatchAsync(
        TranslationRun run,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var failedIds = database.TranslationRunFailures
            .Where(failure => failure.RunId == run.Id)
            .Select(failure => failure.SegmentId);
        return await database.TranslationSegments
            .AsNoTracking()
            .Where(segment =>
                segment.TaskId == run.TaskId &&
                segment.TargetText == null &&
                segment.ConfirmationStatus == SegmentConfirmationStatus.Pending &&
                !failedIds.Contains(segment.Id))
            .OrderBy(segment => segment.Order)
            .Take(Math.Clamp(batchSize, 1, 200))
            .Select(segment => new SegmentWork(
                segment.Id,
                segment.Order,
                segment.SourceText,
                segment.MarkupTableJson))
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessBatchAsync(
        TranslationRun run,
        AiExecutionConfiguration configuration,
        IReadOnlyList<SegmentWork> batch,
        CancellationToken cancellationToken)
    {
        var pending = batch.ToDictionary(segment => segment.Id);
        var lastFailures = pending.Keys.ToDictionary(
            id => id,
            _ => new FailureCause("llm_response_invalid", true));
        var maxAttempts = Math.Clamp(configuration.Provider.MaxAttempts, 1, 10);

        IChatClient chatClient;
        try
        {
            chatClient = chatClientFactory.Create(configuration.Provider, configuration.Model);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var cause = pending.Keys.ToDictionary(
                id => id,
                _ => new FailureCause("llm_model_unavailable", false));
            await RecordFailuresAsync(run.Id, pending.Values, cause, 0, cancellationToken);
            return;
        }

        using (chatClient)
        {
            for (var attempt = 1; attempt <= maxAttempts && pending.Count > 0; attempt++)
            {
                IReadOnlyList<TranslationOutput> outputs;
                try
                {
                    outputs = await RequestTranslationsAsync(
                        chatClient,
                        run.SourceLanguage,
                        run.TargetLanguage,
                        pending.Values.OrderBy(segment => segment.Order).ToArray(),
                        configuration.Provider.RequestTimeout,
                        cancellationToken);
                }
                catch (TranslationExecutionException exception)
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
                    foreach (var segmentId in pending.Keys)
                    {
                        lastFailures[segmentId] = new FailureCause("llm_response_invalid", true);
                    }

                    if (attempt == maxAttempts)
                    {
                        await RecordFailuresAsync(run.Id, pending.Values, lastFailures, attempt, cancellationToken);
                        return;
                    }

                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                var successful = new Dictionary<Guid, string>();
                foreach (var segment in pending.Values)
                {
                    var targetText = outputLookup[segment.Id][0].TargetText;
                    if (string.IsNullOrWhiteSpace(targetText))
                    {
                        lastFailures[segment.Id] = new FailureCause("llm_response_invalid", true);
                        continue;
                    }

                    var violation = rules
                        .Select(rule => rule.Evaluate(new TranslationRuleContext(
                            segment.Id,
                            segment.SourceText,
                            targetText,
                            segment.MarkupTableJson)))
                        .FirstOrDefault(result => result is not null);
                    if (violation is not null)
                    {
                        lastFailures[segment.Id] = new FailureCause(violation.Code, violation.Retryable);
                        continue;
                    }

                    successful[segment.Id] = targetText;
                }

                if (successful.Count > 0)
                {
                    await CommitTranslationsAsync(run.Id, successful, cancellationToken);
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

    private static async Task<IReadOnlyList<TranslationOutput>> RequestTranslationsAsync(
        IChatClient chatClient,
        string? sourceLanguage,
        string targetLanguage,
        IReadOnlyList<SegmentWork> segments,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new
        {
            sourceLanguage,
            targetLanguage,
            segments = segments.Select(segment => new
            {
                segmentId = segment.Id,
                sourceText = segment.SourceText
            })
        }, JsonOptions);
        var messages = new[]
        {
            new ChatMessage(
                ChatRole.System,
                "Translate every segment and return JSON as {\"translations\":[{\"segmentId\":\"uuid\",\"targetText\":\"...\"}]}. Preserve every <xN>, </xN>, and <xN/> token exactly, including order and nesting. Return every requested segment ID exactly once and no additional IDs."),
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
                .GetProperty("translations")
                .EnumerateArray()
                .Select(item => new TranslationOutput(
                    item.GetProperty("segmentId").GetGuid(),
                    item.GetProperty("targetText").GetString() ?? string.Empty))
                .ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationExecutionException(
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
                : new TranslationExecutionException(
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

    private static TranslationExecutionException MapStatus(
        int status,
        bool unsupportedLanguagePair,
        Exception exception)
    {
        if (unsupportedLanguagePair && status is 400 or 422)
        {
            return new TranslationExecutionException(
                "unsupported_language_pair",
                false,
                "The AI provider does not support the requested language pair.",
                exception);
        }

        return status switch
        {
            0 => new TranslationExecutionException(
                "llm_provider_unavailable",
                true,
                "The AI provider is unavailable.",
                exception),
            401 or 403 => new TranslationExecutionException(
                "llm_authentication_failed",
                false,
                "The AI provider rejected the configured credentials.",
                exception),
            404 => new TranslationExecutionException(
                "llm_model_unavailable",
                false,
                "The selected AI model is unavailable.",
                exception),
            408 or 504 => new TranslationExecutionException(
                "llm_provider_timeout",
                true,
                "The AI provider timed out.",
                exception),
            429 or >= 500 => new TranslationExecutionException(
                "llm_provider_unavailable",
                true,
                "The AI provider is unavailable.",
                exception),
            _ => new TranslationExecutionException(
                "llm_response_invalid",
                true,
                "The AI provider rejected the translation request.",
                exception)
        };
    }

    private async Task CommitTranslationsAsync(
        Guid runId,
        IReadOnlyDictionary<Guid, string> translations,
        CancellationToken cancellationToken)
    {
        var segmentIds = translations.Keys.ToArray();
        var segments = await database.TranslationSegments
            .Where(segment => segmentIds.Contains(segment.Id))
            .ToListAsync(cancellationToken);
        var saved = 0;
        foreach (var segment in segments.Where(segment =>
                     segment.TargetText is null && segment.ConfirmationStatus == SegmentConfirmationStatus.Pending))
        {
            segment.TargetText = translations[segment.Id];
            segment.ConfirmationStatus = SegmentConfirmationStatus.Translated;
            segment.Version++;
            saved++;
        }

        var run = await database.TranslationRuns.SingleAsync(entity => entity.Id == runId, cancellationToken);
        run.SucceededSegments += saved;
        run.ProcessedSegments += saved;
        await database.SaveChangesAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    private async Task RecordFailuresAsync(
        Guid runId,
        IEnumerable<SegmentWork> segments,
        IReadOnlyDictionary<Guid, FailureCause> causes,
        int attempts,
        CancellationToken cancellationToken)
    {
        var failures = segments.Select(segment =>
        {
            var cause = causes[segment.Id];
            return new TranslationRunFailure
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                SegmentId = segment.Id,
                SegmentOrder = segment.Order,
                Code = cause.Code,
                Retryable = cause.Retryable,
                Attempts = attempts
            };
        }).ToArray();
        database.TranslationRunFailures.AddRange(failures);
        var run = await database.TranslationRuns.SingleAsync(entity => entity.Id == runId, cancellationToken);
        run.FailedSegments += failures.Length;
        run.ProcessedSegments += failures.Length;
        await database.SaveChangesAsync(cancellationToken);
        database.ChangeTracker.Clear();
    }

    private async Task FinalizeAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await database.TranslationRuns
            .Include(entity => entity.Failures)
            .SingleAsync(entity => entity.Id == runId, cancellationToken);
        var task = await database.TranslationTasks.SingleAsync(entity => entity.Id == run.TaskId, cancellationToken);

        if (run.FailedSegments == 0)
        {
            run.Status = TranslationRunStatus.Completed;
            task.Status = TranslationTaskStatus.Completed;
        }
        else
        {
            run.Status = run.SucceededSegments > 0
                ? TranslationRunStatus.PartialFailed
                : TranslationRunStatus.Failed;
            task.Status = TranslationTaskStatus.Failed;
            var codes = run.Failures.Select(failure => failure.Code).Distinct(StringComparer.Ordinal).ToArray();
            run.FailureCode = codes.Length == 1 ? codes[0] : "segment_translation_failed";
            run.FailureRetryable = run.Failures.Any(failure => failure.Retryable);
        }

        run.ProcessedSegments = run.SelectedSegments;
        run.ActiveTaskLockId = null;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

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

    private static TranslationExecutionException InvalidResponse(Exception? exception = null) => new(
        "llm_response_invalid",
        true,
        "The AI provider returned an invalid structured response.",
        exception);

    private sealed record AiExecutionConfiguration(AiProvider Provider, AiModel Model);
    private sealed record SegmentWork(Guid Id, int Order, string SourceText, string MarkupTableJson);
    private sealed record TranslationOutput(Guid SegmentId, string TargetText);
    private sealed record FailureCause(string Code, bool Retryable);
}

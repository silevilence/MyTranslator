using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;
using MyTranslator.Api.Translation;

namespace MyTranslator.Api.Terms;

public sealed class TermService(AppDbContext database)
{
    public async Task<TermPage> ListAsync(
        string? query,
        string? sourceLanguage,
        string? targetLanguage,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            throw new TermRequestException(
                "invalid_pagination",
                "The page size must be between 1 and 200.",
                StatusCodes.Status400BadRequest);
        }

        var normalizedQuery = TermText.NormalizeOptionalQuery(query);
        var normalizedSourceLanguage = NormalizeOptionalLanguage(sourceLanguage);
        var normalizedTargetLanguage = NormalizeOptionalLanguage(targetLanguage);
        var termQuery = database.Terms.AsNoTracking();
        if (normalizedSourceLanguage is not null)
        {
            termQuery = termQuery.Where(term => term.SourceLanguage == normalizedSourceLanguage);
        }

        if (normalizedTargetLanguage is not null)
        {
            termQuery = termQuery.Where(term => term.TargetLanguage == normalizedTargetLanguage);
        }

        var terms = await termQuery.ToListAsync(cancellationToken);
        var scored = new List<ScoredTerm>(terms.Count);
        foreach (var term in terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TermSearch.Score(term, normalizedQuery) is { } item)
            {
                scored.Add(item);
            }
        }

        IOrderedEnumerable<ScoredTerm> ordered = normalizedQuery is null
            ? scored.OrderByDescending(item => item.Term.UpdatedAt).ThenBy(item => item.Term.Id)
            : scored.OrderByDescending(item => item.MatchScore)
                .ThenByDescending(item => item.Term.UpdatedAt)
                .ThenBy(item => item.Term.Id);

        var position = cursor is null
            ? null
            : TermCursor.Decode(
                cursor,
                normalizedQuery,
                normalizedSourceLanguage,
                normalizedTargetLanguage);
        var afterCursor = position is null
            ? ordered
            : ordered.Where(item => IsAfter(item, position, normalizedQuery is not null));
        var rows = afterCursor.Take(limit + 1).ToList();
        var hasNextPage = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        var items = rows.Select(ToListResponse).ToArray();
        var nextCursor = hasNextPage
            ? TermCursor.Encode(
                normalizedQuery,
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                rows[^1].MatchScore,
                rows[^1].Term.UpdatedAt,
                rows[^1].Term.Id)
            : null;
        return new TermPage(items, nextCursor);
    }

    public async Task<TermResponse> CreateAsync(
        CreateTermRequest request,
        CancellationToken cancellationToken)
    {
        if (await database.Terms.AnyAsync(
                term => term.SourceLanguage == request.SourceLanguage &&
                        term.TargetLanguage == request.TargetLanguage &&
                        term.SourceTermKey == TermText.SourceKey(request.SourceTerm),
                cancellationToken))
        {
            throw await TermConflictAsync(
                request.SourceTerm,
                request.SourceLanguage,
                request.TargetLanguage,
                cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var term = new Term
        {
            Id = Guid.NewGuid(),
            SourceTerm = request.SourceTerm,
            SourceTermKey = TermText.SourceKey(request.SourceTerm),
            TargetTerm = request.TargetTerm,
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            Notes = request.Notes,
            CaseSensitive = request.CaseSensitive,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        database.Terms.Add(term);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraint(exception))
        {
            throw await TermConflictAsync(
                request.SourceTerm,
                request.SourceLanguage,
                request.TargetLanguage,
                cancellationToken);
        }

        return ToResponse(term);
    }

    public async Task<TermResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var term = await database.Terms.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return term is null ? throw TermNotFound() : ToResponse(term);
    }

    public async Task<TermResponse> UpdateAsync(
        Guid id,
        UpdateTermRequest request,
        CancellationToken cancellationToken)
    {
        var term = await database.Terms.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (term is null)
        {
            throw TermNotFound();
        }

        if (term.Version != request.Version)
        {
            throw VersionConflict(request.Version, term.Version);
        }

        var sourceKey = TermText.SourceKey(request.SourceTerm);
        if (await database.Terms.AnyAsync(
                item => item.Id != id &&
                        item.SourceLanguage == request.SourceLanguage &&
                        item.TargetLanguage == request.TargetLanguage &&
                        item.SourceTermKey == sourceKey,
                cancellationToken))
        {
            throw await TermConflictAsync(
                request.SourceTerm,
                request.SourceLanguage,
                request.TargetLanguage,
                cancellationToken);
        }

        term.SourceTerm = request.SourceTerm;
        term.SourceTermKey = sourceKey;
        term.TargetTerm = request.TargetTerm;
        term.SourceLanguage = request.SourceLanguage;
        term.TargetLanguage = request.TargetLanguage;
        term.Notes = request.Notes;
        term.CaseSensitive = request.CaseSensitive;
        term.Version++;
        term.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw await CurrentVersionConflictAsync(id, request.Version, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraint(exception))
        {
            throw await TermConflictAsync(
                request.SourceTerm,
                request.SourceLanguage,
                request.TargetLanguage,
                cancellationToken);
        }

        return ToResponse(term);
    }

    public async Task DeleteAsync(Guid id, int version, CancellationToken cancellationToken)
    {
        var term = await database.Terms.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (term is null)
        {
            throw TermNotFound();
        }

        if (term.Version != version)
        {
            throw VersionConflict(version, term.Version);
        }

        database.Terms.Remove(term);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw await CurrentVersionConflictAsync(id, version, cancellationToken);
        }
    }

    private async Task<TermRequestException> TermConflictAsync(
        string sourceTerm,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var conflictingId = await database.Terms.AsNoTracking()
            .Where(term => term.SourceLanguage == sourceLanguage &&
                           term.TargetLanguage == targetLanguage &&
                           term.SourceTermKey == TermText.SourceKey(sourceTerm))
            .Select(term => term.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return new TermRequestException(
            "term_conflict",
            "The language pair already has this source term.",
            StatusCodes.Status409Conflict,
            new Dictionary<string, object?> { ["conflictingTermId"] = conflictingId });
    }

    internal static TermResponse ToResponse(Term term) => new(
        term.Id,
        term.SourceTerm,
        term.TargetTerm,
        term.SourceLanguage,
        term.TargetLanguage,
        term.Notes,
        term.CaseSensitive,
        term.Version,
        term.CreatedAt,
        term.UpdatedAt);

    private static TermListItemResponse ToListResponse(ScoredTerm item) => new(
        item.Term.Id,
        item.Term.SourceTerm,
        item.Term.TargetTerm,
        item.Term.SourceLanguage,
        item.Term.TargetLanguage,
        item.Term.Notes,
        item.Term.CaseSensitive,
        item.Term.Version,
        item.Term.CreatedAt,
        item.Term.UpdatedAt,
        item.MatchScore,
        item.MatchedField);

    private static bool IsAfter(ScoredTerm item, TermCursorPosition position, bool hasQuery)
    {
        if (hasQuery && item.MatchScore != position.MatchScore)
        {
            return item.MatchScore < position.MatchScore;
        }

        return item.Term.UpdatedAt < position.UpdatedAt ||
               item.Term.UpdatedAt == position.UpdatedAt && item.Term.Id.CompareTo(position.TermId) > 0;
    }

    private static string? NormalizeOptionalLanguage(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!Bcp47LanguageTag.TryNormalize(value.Trim(), out var normalized))
        {
            throw new TermRequestException(
                "invalid_language_tag",
                "The language filter must be a valid BCP 47 language tag.",
                StatusCodes.Status400BadRequest);
        }

        return normalized;
    }

    internal static TermRequestException TermNotFound() => new(
        "term_not_found",
        "Term not found.",
        StatusCodes.Status404NotFound);

    private async Task<TermRequestException> CurrentVersionConflictAsync(
        Guid id,
        int requestedVersion,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var currentVersion = await database.Terms.AsNoTracking()
            .Where(term => term.Id == id)
            .Select(term => (int?)term.Version)
            .SingleOrDefaultAsync(cancellationToken);
        return currentVersion is null
            ? TermNotFound()
            : VersionConflict(requestedVersion, currentVersion.Value);
    }

    private static TermRequestException VersionConflict(int requestedVersion, int currentVersion) => new(
        "term_version_conflict",
        "The term has changed since it was read.",
        StatusCodes.Status409Conflict,
        new Dictionary<string, object?>
        {
            ["requestedVersion"] = requestedVersion,
            ["currentVersion"] = currentVersion
        });

    private static bool IsUniqueConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}

internal static class TermText
{
    public static string SourceKey(string value) => value.ToUpperInvariant();

    public static string? NormalizeOptionalQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.EnumerateRunes().Count() > 500)
        {
            throw new TermRequestException(
                "invalid_term_query",
                "query must not exceed 500 Unicode scalar values.",
                StatusCodes.Status400BadRequest);
        }

        return NormalizeForMatch(trimmed).ToUpperInvariant();
    }

    public static string NormalizeForMatch(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC);
        var result = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(rune.ToString());
        }

        return result.ToString();
    }
}

internal static class TermSearch
{
    public static ScoredTerm? Score(Term term, string? query)
    {
        if (query is null)
        {
            return new ScoredTerm(term, null, null);
        }

        var sourceScore = ScoreCandidate(TermText.NormalizeForMatch(term.SourceTerm).ToUpperInvariant(), query);
        var targetScore = ScoreCandidate(TermText.NormalizeForMatch(term.TargetTerm).ToUpperInvariant(), query);
        var score = Math.Max(sourceScore, targetScore);
        if (score < 0.6)
        {
            return null;
        }

        return new ScoredTerm(
            term,
            Math.Round(score, 4, MidpointRounding.AwayFromZero),
            sourceScore >= targetScore ? "source" : "target");
    }

    private static double ScoreCandidate(string candidate, string query)
    {
        if (candidate.Equals(query, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (candidate.Contains(query, StringComparison.Ordinal))
        {
            return 0.9;
        }

        if (query.Contains(candidate, StringComparison.Ordinal))
        {
            return 0.8;
        }

        var candidateRunes = candidate.EnumerateRunes().ToArray();
        var queryRunes = query.EnumerateRunes().ToArray();
        var maximumLength = Math.Max(candidateRunes.Length, queryRunes.Length);
        return maximumLength == 0
            ? 1.0
            : 1.0 - (double)LevenshteinDistance(candidateRunes, queryRunes) / maximumLength;
    }

    private static int LevenshteinDistance(Rune[] left, Rune[] right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}

internal sealed record ScoredTerm(Term Term, double? MatchScore, string? MatchedField);

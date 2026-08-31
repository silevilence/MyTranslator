using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;
using MyTranslator.Api.Text;
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

        var position = cursor is null
            ? null
            : TermCursor.Decode(
                cursor,
                normalizedQuery,
                normalizedSourceLanguage,
                normalizedTargetLanguage);
        if (normalizedQuery is null)
        {
            return await ListWithoutQueryAsync(
                termQuery,
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                limit,
                position,
                cancellationToken);
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

        var ordered = scored.OrderByDescending(item => item.MatchScore)
            .ThenByDescending(item => item.Term.UpdatedAt)
            .ThenBy(item => item.Term.Id);
        var afterCursor = position is null
            ? ordered
            : ordered.Where(item => IsAfter(item, position));
        var rows = afterCursor.Take(limit + 1).ToList();
        return CreatePage(
            rows,
            normalizedQuery,
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            limit);
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
            UpdatedAt = now,
            UpdatedAtSortKey = TermSortKey.From(now)
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
        term.UpdatedAtSortKey = TermSortKey.From(term.UpdatedAt);
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

    private static async Task<TermPage> ListWithoutQueryAsync(
        IQueryable<Term> termQuery,
        string? sourceLanguage,
        string? targetLanguage,
        int limit,
        TermCursorPosition? position,
        CancellationToken cancellationToken)
    {
        if (position is not null)
        {
            var updatedAtSortKey = TermSortKey.From(position.UpdatedAt);
            termQuery = termQuery.Where(term =>
                string.Compare(term.UpdatedAtSortKey, updatedAtSortKey) < 0 ||
                term.UpdatedAtSortKey == updatedAtSortKey && term.Id.CompareTo(position.TermId) > 0);
        }

        var terms = await termQuery
            .OrderByDescending(term => term.UpdatedAtSortKey)
            .ThenBy(term => term.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var rows = terms
            .Select(term => new ScoredTerm(term, null, null))
            .ToList();
        return CreatePage(rows, null, sourceLanguage, targetLanguage, limit);
    }

    private static TermPage CreatePage(
        List<ScoredTerm> rows,
        string? query,
        string? sourceLanguage,
        string? targetLanguage,
        int limit)
    {
        var hasNextPage = rows.Count > limit;
        rows = rows.Take(limit).ToList();
        var items = rows.Select(ToListResponse).ToArray();
        var nextCursor = hasNextPage
            ? TermCursor.Encode(
                query,
                sourceLanguage,
                targetLanguage,
                rows[^1].MatchScore,
                rows[^1].Term.UpdatedAt,
                rows[^1].Term.Id)
            : null;
        return new TermPage(items, nextCursor);
    }

    internal static TermResponse ToResponse(Term term) => new()
    {
        Id = term.Id,
        SourceTerm = term.SourceTerm,
        TargetTerm = term.TargetTerm,
        SourceLanguage = term.SourceLanguage,
        TargetLanguage = term.TargetLanguage,
        Notes = term.Notes,
        CaseSensitive = term.CaseSensitive,
        Version = term.Version,
        CreatedAt = term.CreatedAt,
        UpdatedAt = term.UpdatedAt
    };

    private static TermListItemResponse ToListResponse(ScoredTerm item) => new()
    {
        Id = item.Term.Id,
        SourceTerm = item.Term.SourceTerm,
        TargetTerm = item.Term.TargetTerm,
        SourceLanguage = item.Term.SourceLanguage,
        TargetLanguage = item.Term.TargetLanguage,
        Notes = item.Term.Notes,
        CaseSensitive = item.Term.CaseSensitive,
        Version = item.Term.Version,
        CreatedAt = item.Term.CreatedAt,
        UpdatedAt = item.Term.UpdatedAt,
        MatchScore = item.MatchScore,
        MatchedField = item.MatchedField
    };

    private static bool IsAfter(ScoredTerm item, TermCursorPosition position)
    {
        if (item.MatchScore != position.MatchScore)
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

    public static string NormalizeForMatch(string value) => TextSimilarity.NormalizeForMatch(value);
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

        return TextSimilarity.LevenshteinScore(candidate, query);
    }
}

internal sealed record ScoredTerm(Term Term, double? MatchScore, string? MatchedField);

internal static class TermSortKey
{
    // Keep the format and original offset identical to EF Core SQLite's
    // DateTimeOffset text encoding so migrated and newly written keys are equal.
    private const string Format = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz";

    public static string From(DateTimeOffset value) =>
        value.ToString(Format, CultureInfo.InvariantCulture);
}

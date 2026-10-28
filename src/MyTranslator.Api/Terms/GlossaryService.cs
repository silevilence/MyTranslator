using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;
using MyTranslator.Api.Pagination;

namespace MyTranslator.Api.Terms;

public sealed record GlossaryResponse(Guid Id, string Name, int Version);
public sealed record GlossaryPage(IReadOnlyList<GlossaryResponse> Items, string? NextCursor);

public sealed class GlossaryService(AppDbContext database)
{
    public async Task<GlossaryPage> ListAsync(int limit, string? cursor, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200) throw Invalid("invalid_pagination");
        var query = database.Glossaries.AsNoTracking();
        if (cursor is not null)
        {
            try
            {
                var position = OpaqueCursorCodec.Decode<Position>(cursor);
                if (position.Resource != "glossaries" || position.Id == Guid.Empty) throw Invalid("invalid_cursor");
                query = query.Where(g => g.Id.CompareTo(position.Id) > 0);
            }
            catch (OpaqueCursorCodecException) { throw Invalid("invalid_cursor"); }
        }
        var rows = await query.OrderBy(g => g.Id).Take(limit + 1).ToArrayAsync(cancellationToken);
        return new(rows.Take(limit).Select(ToResponse).ToArray(), rows.Length > limit
            ? OpaqueCursorCodec.Encode(new Position("glossaries", rows[limit - 1].Id)) : null);
    }

    public async Task<GlossaryResponse> CreateAsync(string? name, CancellationToken cancellationToken)
    {
        var glossary = new Glossary { Id = Guid.NewGuid(), Name = ValidateName(name) };
        database.Glossaries.Add(glossary);
        await database.SaveChangesAsync(cancellationToken);
        return ToResponse(glossary);
    }

    public async Task<GlossaryResponse> UpdateAsync(Guid id, string? name, int version, CancellationToken cancellationToken)
    {
        var glossary = await GetForWriteAsync(id, version, cancellationToken);
        glossary.Name = ValidateName(name);
        glossary.Version++;
        await SaveAsync(cancellationToken);
        return ToResponse(glossary);
    }

    public async Task DeleteAsync(Guid id, int version, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var glossary = await GetForWriteAsync(id, version, cancellationToken);
        if (id == Glossary.DefaultId || await database.Terms.AnyAsync(t => t.GlossaryId == id, cancellationToken) ||
            await database.TranslationTasks.AnyAsync(t => t.GlossaryId == id, cancellationToken))
            throw new TermRequestException("glossary_in_use", "The glossary is in use or protected.", 409);
        database.Glossaries.Remove(glossary);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task RequireAsync(AppDbContext database, Guid id, CancellationToken cancellationToken)
    {
        if (!await database.Glossaries.AnyAsync(g => g.Id == id, cancellationToken))
            throw new TermRequestException("glossary_not_found", "Glossary not found.", 404);
    }

    private async Task<Glossary> GetForWriteAsync(Guid id, int version, CancellationToken cancellationToken)
    {
        if (version < 1) throw Invalid("invalid_glossary_version");
        var glossary = await database.Glossaries.SingleOrDefaultAsync(g => g.Id == id, cancellationToken)
            ?? throw new TermRequestException("glossary_not_found", "Glossary not found.", 404);
        if (glossary.Version != version) throw Conflict();
        return glossary;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
    }

    private static string ValidateName(string? name) => name?.Trim() is { Length: > 0 and <= 100 } value
        ? value : throw Invalid("invalid_glossary_name");
    private static GlossaryResponse ToResponse(Glossary glossary) => new(glossary.Id, glossary.Name, glossary.Version);
    private static TermRequestException Invalid(string code) => new(code, "Invalid glossary request.", 400);
    private static TermRequestException Conflict() => new("glossary_version_conflict", "Glossary changed.", 409);
    private sealed record Position(string Resource, Guid Id);
}

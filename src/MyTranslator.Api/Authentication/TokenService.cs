using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Authentication;

public sealed class TokenService(AppDbContext database, IHostEnvironment environment)
{
    public async Task<ApiToken?> FindActiveAsync(string tokenValue, CancellationToken cancellationToken)
    {
        if (!TokenValue.IsValid(tokenValue, environment.IsDevelopment()))
        {
            return null;
        }

        var tokenHash = TokenValue.Hash(tokenValue);
        return await database.ApiTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(
                token => token.TokenHash == tokenHash && token.RevokedAt == null,
                cancellationToken);
    }

    public async Task<(ApiToken Token, string Value)> CreateAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var value = TokenValue.Generate();
        var token = new ApiToken
        {
            Id = Guid.NewGuid(),
            Name = name,
            TokenHash = TokenValue.Hash(value),
            TokenPrefix = TokenValue.GetDisplayPrefix(value),
            CreatedAt = DateTimeOffset.UtcNow
        };

        database.ApiTokens.Add(token);
        await database.SaveChangesAsync(cancellationToken);
        return (token, value);
    }

    public async Task<List<ApiToken>> ListAsync(CancellationToken cancellationToken)
    {
        var tokens = await database.ApiTokens.AsNoTracking().ToListAsync(cancellationToken);
        return tokens.OrderByDescending(token => token.CreatedAt).ToList();
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await database.ApiTokens.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (token is null)
        {
            return false;
        }

        if (token.RevokedAt is null)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}

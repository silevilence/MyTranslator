using Microsoft.EntityFrameworkCore;
using MyTranslator.Api.Authentication;

namespace MyTranslator.Api.Data;

public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.MigrateAsync(cancellationToken);

        var initialToken = configuration["INITIAL_TOKEN"];
        if (string.IsNullOrWhiteSpace(initialToken) && environment.IsDevelopment())
        {
            initialToken = TokenAuthenticationDefaults.DevelopmentToken;
            logger.LogWarning(
                "INITIAL_TOKEN is not configured. The fixed development token {DevelopmentToken} is enabled.",
                TokenAuthenticationDefaults.DevelopmentToken);
        }

        if (string.IsNullOrWhiteSpace(initialToken))
        {
            throw new InvalidOperationException("INITIAL_TOKEN must be configured outside Development.");
        }

        if (!TokenValue.IsValid(initialToken, environment.IsDevelopment()))
        {
            var expectedFormat = environment.IsDevelopment()
                ? "sk-{32 hexadecimal characters} or sk-dev-{32 hexadecimal characters}"
                : "sk-{32 hexadecimal characters}";
            throw new InvalidOperationException($"INITIAL_TOKEN must match {expectedFormat}.");
        }

        var tokenHash = TokenValue.Hash(initialToken);
        if (await database.ApiTokens.AnyAsync(token => token.TokenHash == tokenHash, cancellationToken))
        {
            return;
        }

        database.ApiTokens.Add(new ApiToken
        {
            Id = Guid.NewGuid(),
            Name = environment.IsDevelopment() ? "Development token" : "Initial token",
            TokenHash = tokenHash,
            TokenPrefix = TokenValue.GetDisplayPrefix(initialToken),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

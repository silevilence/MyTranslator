namespace MyTranslator.Api.Data;

public sealed class ApiToken
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    public required string TokenHash { get; init; }

    public required string TokenPrefix { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }
}

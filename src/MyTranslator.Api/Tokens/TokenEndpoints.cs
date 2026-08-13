using MyTranslator.Api.Authentication;

namespace MyTranslator.Api.Tokens;

public static class TokenEndpoints
{
    public static IEndpointRouteBuilder MapTokenEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tokens").WithTags("Tokens");

        group.MapGet("/", async (TokenService tokenService, CancellationToken cancellationToken) =>
        {
            var tokens = await tokenService.ListAsync(cancellationToken);
            return Results.Ok(tokens.Select(token => new TokenResponse(
                token.Id,
                token.Name,
                token.TokenPrefix,
                token.CreatedAt,
                token.RevokedAt)));
        });

        group.MapPost("/", async (
            CreateTokenRequest request,
            TokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Name is required and must not exceed 100 characters."]
                });
            }

            var (token, value) = await tokenService.CreateAsync(name, cancellationToken);
            return Results.Created(
                $"/api/tokens/{token.Id}",
                new CreatedTokenResponse(token.Id, token.Name, value));
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            TokenService tokenService,
            CancellationToken cancellationToken) =>
            await tokenService.RevokeAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound());

        return endpoints;
    }

    private sealed record CreateTokenRequest(string? Name);

    private sealed record CreatedTokenResponse(Guid Id, string Name, string Token);

    private sealed record TokenResponse(
        Guid Id,
        string Name,
        string TokenPrefix,
        DateTimeOffset CreatedAt,
        DateTimeOffset? RevokedAt);
}

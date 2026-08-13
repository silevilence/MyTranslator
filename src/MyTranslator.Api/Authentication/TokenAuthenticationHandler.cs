using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MyTranslator.Api.Authentication;

public sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokenService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            return AuthenticateResult.NoResult();
        }

        var authorization = authorizationValues.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("The Authorization header must use the Bearer scheme.");
        }

        var tokenValue = authorization["Bearer ".Length..].Trim();
        var token = await tokenService.FindActiveAsync(tokenValue, Context.RequestAborted);
        if (token is null)
        {
            return AuthenticateResult.Fail("The API token is invalid or revoked.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, token.Id.ToString()),
                new Claim(ClaimTypes.Name, token.Name)
            ],
            Scheme.Name);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

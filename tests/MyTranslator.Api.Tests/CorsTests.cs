using System.Net;

namespace MyTranslator.Api.Tests;

public sealed class CorsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task DevelopmentAllowsBrowserPreflightForApiEndpoints()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/health");
        request.Headers.Add("Origin", "http://localhost:5000");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        using var client = factory.CreateClient();
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(
            "authorization",
            response.Headers.GetValues("Access-Control-Allow-Headers").Single(),
            StringComparison.OrdinalIgnoreCase);
    }
}

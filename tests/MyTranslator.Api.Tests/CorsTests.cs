using System.Net;

namespace MyTranslator.Api.Tests;

public sealed class CorsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task BrowserCanReadDownloadNameAndRunResponseHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "http://localhost:5000");
        request.Headers.Add("Authorization", "Bearer sk-dev-00000000000000000000000000000000");
        using var client = factory.CreateClient();
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var exposed = response.Headers.GetValues("Access-Control-Expose-Headers").Single();
        foreach (var header in new[] { "Content-Disposition", "Location", "Retry-After" })
            Assert.Contains(header, exposed, StringComparison.OrdinalIgnoreCase);
    }

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

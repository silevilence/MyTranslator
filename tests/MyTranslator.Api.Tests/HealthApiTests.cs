using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MyTranslator.Api.Tests;

public sealed class HealthApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task DevelopmentTokenCanAccessProtectedHealthEndpoint()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "sk-dev-00000000000000000000000000000000");

        var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("healthy", body?.Status);
    }

    private sealed record HealthResponse(string Status);
}

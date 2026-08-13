using System.Net;
using System.Net.Http.Json;

namespace MyTranslator.Api.Tests;

public sealed class AuthenticationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task ConfiguredInitialTokenIsUsedOutsideDevelopment()
    {
        const string initialToken = "sk-11111111111111111111111111111111";
        await using var productionFactory = new ApiFactory("Production", initialToken);
        using var client = productionFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", initialToken);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public void DevelopmentTokenCannotBootstrapProduction()
    {
        using var productionFactory = new ApiFactory(
            "Production",
            "sk-dev-00000000000000000000000000000000");

        var exception = Assert.Throws<InvalidOperationException>(productionFactory.CreateClient);

        Assert.Contains("INITIAL_TOKEN", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer sk-ffffffffffffffffffffffffffffffff")]
    [InlineData("Basic abc123")]
    public async Task MissingOrInvalidTokenReturnsUnifiedUnauthorizedProblem(string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var client = factory.CreateClient();
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Unauthorized", body?.Title);
        Assert.Equal(401, body?.Status);
        Assert.Equal("/api/health", body?.Instance);
    }

    private sealed record ProblemResponse(string Title, int Status, string Instance);
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyTranslator.Api.Tests;

public sealed class TokenManagementApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string DevelopmentToken = "sk-dev-00000000000000000000000000000000";

    [Fact]
    public async Task TokenCanBeGeneratedUsedListedAndRevoked()
    {
        using var administrator = CreateClient(DevelopmentToken);
        var createResponse = await administrator.PostAsJsonAsync(
            "/api/tokens",
            new { name = "External integration" });
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedTokenResponse>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(created);
        Assert.Matches("^sk-[0-9a-f]{32}$", created.Token);
        Assert.Equal("External integration", created.Name);

        using var generatedTokenClient = CreateClient(created.Token);
        Assert.Equal(HttpStatusCode.OK, (await generatedTokenClient.GetAsync("/api/health")).StatusCode);

        var listResponse = await administrator.GetAsync("/api/tokens");
        var listJson = await listResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = JsonSerializer.Deserialize<List<ListedTokenResponse>>(
            listJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains(listed!, token =>
            token.Id == created.Id &&
            token.Name == created.Name &&
            token.TokenPrefix == created.Token[..15] &&
            token.RevokedAt is null);
        Assert.DoesNotContain(created.Token, listJson, StringComparison.Ordinal);

        var revokeResponse = await administrator.DeleteAsync($"/api/tokens/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await generatedTokenClient.GetAsync("/api/health")).StatusCode);
    }

    private HttpClient CreateClient(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed record CreatedTokenResponse(Guid Id, string Name, string Token);

    private sealed record ListedTokenResponse(
        Guid Id,
        string Name,
        string TokenPrefix,
        DateTimeOffset CreatedAt,
        DateTimeOffset? RevokedAt);
}

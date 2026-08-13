using System.Net;
using System.Text.Json;

namespace MyTranslator.Api.Tests;

public sealed class OpenApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task SwaggerDocumentDescribesApiRoutesAndBearerAuthentication()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(root.GetProperty("paths").TryGetProperty("/api/health", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty("/api/tokens", out _));
        Assert.True(
            root.GetProperty("components")
                .GetProperty("securitySchemes")
                .TryGetProperty("Bearer", out _));
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Components;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;

namespace MyTranslator.Shared.Tests;

public sealed class TokenManagementTests
{
    [Fact]
    public async Task CreateListAndRevokeUsePublicEndpoints()
    {
        string? sent = null;
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.Created, new CreatedApiToken(id, "test", "sk-test"));
            }
            return request.Method == HttpMethod.Delete ? new(HttpStatusCode.NoContent)
                : Json(HttpStatusCode.OK, new[] { new ApiTokenSummary(id, "test", "sk-prefix", DateTimeOffset.UtcNow, null) });
        });
        var service = Create(handler);
        Assert.Equal("sk-test", (await service.CreateAsync(" test ")).Token);
        Assert.Equal("sk-prefix", Assert.Single(await service.ListAsync()).TokenPrefix);
        await service.RevokeAsync(id);
        Assert.Equal("test", JsonDocument.Parse(sent!).RootElement.GetProperty("name").GetString());
        Assert.Equal($"/api/tokens/{id}", handler.Requests.Last().RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task ServicePropagatesCreationAndRevocationFailures(int status)
    {
        var service = Create(new StubHttpMessageHandler(_ => Json((HttpStatusCode)status, new { status })));
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiErrorException>(() => service.CreateAsync("test"))).StatusCode);
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiErrorException>(() => service.RevokeAsync(Guid.NewGuid()))).StatusCode);
    }

    [Theory]
    [InlineData(0, 0, 0, 100, 100)]
    [InlineData(3, 2, 1, 66.7, 33.3)]
    [InlineData(3, 3, 0, 100, 0)]
    public void DashboardSeparatesCompletionFromConfirmation(int total, int completed, int confirmed, double percent, double confirmedPercent)
    {
        var progress = new TaskProgress { TotalSegments = total, CompletedSegments = completed, ConfirmedSegments = confirmed };
        Assert.Equal(percent, progress.Percent);
        Assert.Equal(confirmedPercent, progress.ConfirmedPercent);
    }

    [Fact]
    public void FeatureComponentResourcesResolveInRuntimeNamespaces()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Microsoft.Extensions.Options.Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        (Type Component, string Key)[] cases = [(typeof(SegmentList), "Source"), (typeof(SegmentEditorPanel), "Save"),
            (typeof(TranslationEditor), "RefreshDiagnostics"), (typeof(KeyboardHelp), "Keys"), (typeof(TermHint), "Title"),
            (typeof(TokenManagementPanel), "Title"), (typeof(ProgressBoard), "Completed"), (typeof(TaskDashboard), "Title")];
        foreach (var item in cases) Assert.False(factory.Create(item.Component)[item.Key].ResourceNotFound, item.Component.FullName);
    }

    private static TokenManagementService Create(StubHttpMessageHandler handler)
    {
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost") },
            new AuthStateProvider(new TokenStore(new FakeJSRuntime())), new FakeNavigationManager("http://localhost/"), NullLogger<ApiClient>.Instance);
        return new(api, new ConfigurationBuilder().Build());
    }
    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(body, ImportJson.Options), Encoding.UTF8, "application/json") };
}

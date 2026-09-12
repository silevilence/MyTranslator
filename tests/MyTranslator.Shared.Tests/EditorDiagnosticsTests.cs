using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;

namespace MyTranslator.Shared.Tests;

public sealed class EditorDiagnosticsTests
{
    [Fact]
    public void FindingsRequireMatchingTaskRevisionAndSegmentVersion()
    {
        var taskId = Guid.NewGuid();
        var segment = new Segment { Id = Guid.NewGuid(), Version = 2 };
        var rules = new RuleCheckResponse(taskId, 3, 1, 1, ["missing_translation"],
            [new(segment.Id, 1, 2, [new("missing_translation", "missing_translation", "targetText", 0, 0)])]);
        Assert.Single(EditorDiagnostics.RulesFor(rules, taskId, 3, segment));
        Assert.Empty(EditorDiagnostics.RulesFor(rules, taskId, 4, segment));
        Assert.Empty(EditorDiagnostics.RulesFor(rules, Guid.NewGuid(), 3, segment));
        Assert.Empty(EditorDiagnostics.RulesFor(rules, taskId, 3, segment with { Version = 3 }));
        Assert.Empty(EditorDiagnostics.RulesFor(null, taskId, 3, segment));
    }

    [Fact]
    public void TerminologyRequiresMatchingLanguagePairAndSavedVersion()
    {
        var taskId = Guid.NewGuid();
        var segment = new Segment { Id = Guid.NewGuid(), Version = 2 };
        var terms = new TermAlignmentResponse(taskId, 1, "en", "zh-CN",
            [new(segment.Id, 1, 2, [new(Guid.NewGuid(), "Hello", "你好", false, 1, 0)])]);
        Assert.Single(EditorDiagnostics.TermsFor(terms, taskId, 1, segment, new("EN", "zh-cn")));
        Assert.Empty(EditorDiagnostics.TermsFor(terms, taskId, 1, segment, new("fr", "zh-CN")));
        Assert.Empty(EditorDiagnostics.TermsFor(terms, taskId, 1, segment with { Version = 3 }, new("en", "zh-CN")));
        Assert.Empty(EditorDiagnostics.TermsFor(terms, taskId, 2, segment, new("en", "zh-CN")));
        Assert.Empty(EditorDiagnostics.TermsFor(null, taskId, 1, segment, new("en", "zh-CN")));
    }

    [Fact]
    public async Task ClientSendsRevisionAndLanguageContextAndPreservesErrors()
    {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.Conflict)
                { Content = new StringContent("{\"code\":\"extraction_revision_changed\"}", Encoding.UTF8, "application/json") };
        });
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost") },
            new AuthStateProvider(new TokenStore(new FakeJSRuntime())), new FakeNavigationManager("http://localhost/"), NullLogger<ApiClient>.Instance);
        var service = new EditorDiagnosticsService(api, new ConfigurationBuilder().Build());
        var id = Guid.NewGuid();
        Assert.Equal("extraction_revision_changed", (await Assert.ThrowsAsync<ApiErrorException>(() => service.CheckRulesAsync(id, 5))).Code);
        await Assert.ThrowsAsync<ApiErrorException>(() => service.CheckTermsAsync(id, 5, new("en", "zh-CN")));
        using var body = JsonDocument.Parse(requests[1]);
        Assert.Equal(5, body.RootElement.GetProperty("extractionRevision").GetInt32());
        Assert.Equal("en", body.RootElement.GetProperty("sourceLanguage").GetString());
        Assert.Equal("zh-CN", body.RootElement.GetProperty("targetLanguage").GetString());
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyTranslator.Api.Data;
using MyTranslator.Api.Rules;

namespace MyTranslator.Api.Tests;

public sealed class RuleCheckTests
{
    private const string Markup = """[{"id":1,"kind":"paired"},{"id":2,"kind":"standalone"},{"id":3,"kind":"paired"}]""";
    private const string Source = "<x1>Hello <x3>world</x3><x2/></x1>";

    [Theory]
    [InlineData("<x1>你好 <x3>世界</x3><x2/></x1>", true)]
    [InlineData("<x1>你好 <x3>世界</x1><x2/></x3>", false)]
    [InlineData("<x1>你好 <x3>世界</x3><x2></x1>", false)]
    [InlineData("<x1>你好 <x3>世界</x3></x1>", false)]
    [InlineData("<x1>你好 <x3>世界</x3><x2/><x2/></x1>", false)]
    [InlineData("<x1>你好 <x3>世界</x3><x2/></x1><x99/>", false)]
    [InlineData("<x1>你好 <x2/><x3>世界</x3></x1>", false)]
    public void PlaceholderRuleChecksKindsCountsOrderAndNesting(string target, bool valid)
    {
        var result = new PlaceholderIntegrityRule().Evaluate(new(Guid.NewGuid(), Source, target, Markup));
        Assert.Equal(valid, result is null);
    }

    [Fact]
    public void CorruptSourceNestingCannotAuthorizeCorruptTarget()
    {
        const string invalid = "<x1><x3>text</x1></x3><x2/>";
        Assert.NotNull(new PlaceholderIntegrityRule().Evaluate(new(Guid.NewGuid(), invalid, invalid, Markup)));
        Assert.NotNull(new PlaceholderIntegrityRule().Evaluate(new(Guid.NewGuid(), "<x1>text", "<x1>text", Markup)));
        Assert.Null(new PlaceholderIntegrityRule().Evaluate(new(Guid.NewGuid(), Source, null, Markup)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" \r\n", true)]
    [InlineData("<x1><x3> </x3><x2/></x1>", true)]
    [InlineData("Hello", false)]
    [InlineData("<x99/>", false)]
    public void MissingTranslationOnlyReportsDeterministicMissingText(string? target, bool missing)
    {
        Assert.Equal(missing, new MissingTranslationRule().Evaluate(new(Guid.NewGuid(), Source, target, Markup)) is not null);
    }

    [Fact]
    public void EngineDiscoversExtensionAndHonorsConfiguration()
    {
        ITranslationRule[] rules = [new PlaceholderIntegrityRule(), new MissingTranslationRule(), new TestRule()];
        var engine = new RuleEngine(rules, Options.Create(new RuleCheckOptions { DisabledRules = ["missing_translation"] }));
        var result = Assert.Single(engine.Evaluate(new(Guid.NewGuid(), "Hi", null, "[]")));
        Assert.Equal("custom", result.RuleId);
        Assert.Equal(2, result.Offset);
        Assert.Equal(["placeholder_integrity", "custom"], engine.EnabledRules);
    }

    [Fact]
    public async Task ChecksSavedSnapshotAndDoesNotMutateSegments()
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var taskId = await ImportAsync(client, "One\n\nTwo\n\nThree");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.TranslationSegments.OrderBy(s => s.Order).ToListAsync();
            rows[1].SourceText = Source;
            rows[1].MarkupTableJson = Markup;
            rows[1].TargetText = "broken";
            rows[2].TargetText = "Translated";
            await db.SaveChangesAsync();
        }
        var response = await client.PostAsJsonAsync($"/api/tasks/{taskId}/rule-checks", new { extractionRevision = 1 });
        response.EnsureSuccessStatusCode();
        var check = (await response.Content.ReadFromJsonAsync<RuleCheckResponse>())!;
        Assert.Equal(3, check.TotalSegments);
        Assert.Equal(2, check.ViolatingSegments);
        Assert.Equal("missing_translation", Assert.Single(check.Items[0].Violations).Code);
        Assert.Equal("placeholder_integrity_violation", Assert.Single(check.Items[1].Violations).Code);
        Assert.All(check.Items, item => Assert.Equal(1, item.Version));
        var second = await client.PostAsJsonAsync($"/api/tasks/{taskId}/rule-checks", new { extractionRevision = 1 });
        Assert.Equal(await response.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        var task = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}");
        Assert.Equal("created", task.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(0, "invalid_extraction_revision", HttpStatusCode.BadRequest)]
    [InlineData(2, "extraction_revision_changed", HttpStatusCode.Conflict)]
    public async Task RejectsInvalidOrStaleRevision(int revision, string code, HttpStatusCode status)
    {
        using var factory = new ApiFactory();
        using var client = CreateClient(factory);
        var taskId = await ImportAsync(client, "Hi");
        var response = await client.PostAsJsonAsync($"/api/tasks/{taskId}/rule-checks", new { extractionRevision = revision });
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task RequiresAuthenticationAndExistingTask()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var url = $"/api/tasks/{Guid.NewGuid()}/rule-checks";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, new { extractionRevision = 1 })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "sk-dev-00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(url, new { extractionRevision = 1 })).StatusCode);
    }

    internal static HttpClient CreateClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "sk-dev-00000000000000000000000000000000");
        return client;
    }

    internal static async Task<Guid> ImportAsync(HttpClient client, string text, string fileName = "sample.txt")
    {
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Add(file, "file", fileName);
        var response = await client.PostAsync("/api/tasks/imports/file", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetGuid();
    }

    private sealed class TestRule : ITranslationRule
    {
        public string Id => "custom";
        public TranslationRuleViolation? Evaluate(TranslationRuleContext context) => new("custom_violation", false, 2, 1);
    }
}

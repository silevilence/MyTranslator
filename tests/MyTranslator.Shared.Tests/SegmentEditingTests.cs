using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTranslator.Shared.Models;
using MyTranslator.Shared.Services;

namespace MyTranslator.Shared.Tests;

public sealed class SegmentEditingTests
{
    [Fact]
    public void DraftProtectsMarkupAndPreservesTextExactly()
    {
        var original = new Segment { Id = Guid.NewGuid(), Version = 4, SourceText = "<x1>Hello</x1>", TargetText = " <x1>你好</x1> ",
            MarkupTable = [new() { Id = 1, Kind = "paired", OpeningText = "<b>", ClosingText = "</b>" }] };
        var draft = new SegmentDraft(original);
        Assert.False(draft.IsDirty);
        Assert.Equal(original.TargetText, draft.TargetText);
        Assert.Throws<InvalidOperationException>(() => draft.SetText(1, "delete marker"));
        draft.SetText(2, "您好");
        Assert.True(draft.IsDirty);
        Assert.Equal(" <x1>您好</x1> ", draft.TargetText);
        Assert.Equal(" <x1>你好</x1> ", original.TargetText);
        Assert.Equal(4, draft.Original.Version);
    }

    [Fact]
    public void NewDraftUsesSourceMarkersWithoutCopyingSourceAsTranslation()
    {
        var draft = new SegmentDraft(new Segment { SourceText = "# <x1>Hello</x1> world<x2/>",
            MarkupTable = [new() { Id = 1, Kind = "paired" }, new() { Id = 2, Kind = "standalone" }] });
        Assert.Null(draft.TargetText);
        Assert.False(draft.IsDirty);
        draft.SetText(2, "你好");
        Assert.Equal("<x1>你好</x1><x2/>", draft.TargetText);
        draft.SetText(2, null);
        Assert.Null(draft.TargetText);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void UnregisteredLiteralIsEditableAndWhitespaceClearIsDirty()
    {
        var draft = new SegmentDraft(new Segment { SourceText = "Literal <x1>", TargetText = "字面 <x1>" });
        Assert.Single(draft.Parts);
        draft.SetText(0, " ");
        Assert.True(draft.IsDirty);
        Assert.Null(draft.TargetText);
        Assert.Null(new SegmentDraft(new Segment()).TargetText);
    }

    [Fact]
    public async Task SaveAndBatchSendOnlyWritableFieldsAndReturnUpdatedVersion()
    {
        var requests = new List<(string Url, string Body)>();
        var id = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            var row = new Segment { Id = id, Version = 2, TargetText = "你好", ConfirmationStatus = "confirmed" };
            return Json(HttpStatusCode.OK, request.Method == HttpMethod.Put ? row : new[] { row });
        });
        var service = Create(handler);
        var saved = await service.SaveAsync(id, id, new(3, 1, "你好", "confirmed", "en", "zh-CN"));
        var batch = await service.ConfirmAsync(id, new(3, false, [new(id, 2)]));
        Assert.Equal(2, saved.Version);
        Assert.Single(batch);
        using var body = JsonDocument.Parse(requests[0].Body);
        Assert.Equal(3, body.RootElement.GetProperty("extractionRevision").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("version").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("markupTable", out _));
        Assert.Equal($"/api/tasks/{id}/segment-confirmations", requests[1].Url);
    }

    [Theory]
    [InlineData(409, "segment_version_conflict")]
    [InlineData(422, "placeholder_integrity_violation")]
    [InlineData(401, "unauthorized")]
    public async Task FailedSaveLeavesCallerDraftIntact(int status, string code)
    {
        var service = Create(new StubHttpMessageHandler(_ => Json((HttpStatusCode)status, new { code })));
        var draft = new SegmentDraft(new Segment { Id = Guid.NewGuid(), Version = 3, TargetText = "old" });
        draft.SetText(0, "unsaved");
        var exception = await Assert.ThrowsAsync<ApiErrorException>(() => service.SaveAsync(Guid.NewGuid(), draft.Original.Id, new(1, 3, draft.TargetText, "translated")));
        Assert.Equal(code, exception.Code);
        Assert.Equal("unsaved", draft.TargetText);
        Assert.Equal(3, draft.Original.Version);
    }

    private static SegmentEditingService Create(StubHttpMessageHandler handler)
    {
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost") },
            new AuthStateProvider(new TokenStore(new FakeJSRuntime())), new FakeNavigationManager("http://localhost/"), NullLogger<ApiClient>.Instance);
        return new(api, new ConfigurationBuilder().Build());
    }
    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(body, ImportJson.Options), Encoding.UTF8, "application/json") };
}

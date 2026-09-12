using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>规则与术语检查的只读快照客户端，不修改分段或确认状态。</summary>
public sealed class EditorDiagnosticsService(ApiClient api, IConfiguration configuration) : RunApiClientBase(api, configuration)
{
    public Task<RuleCheckResponse> CheckRulesAsync(Guid taskId, int revision, CancellationToken cancellationToken = default) =>
        PostAsync<RuleCheckResponse>($"/api/tasks/{taskId}/rule-checks", new { extractionRevision = revision }, cancellationToken);
    public Task<TermAlignmentResponse> CheckTermsAsync(Guid taskId, int revision, LanguagePair language, CancellationToken cancellationToken = default) =>
        PostAsync<TermAlignmentResponse>($"/api/tasks/{taskId}/term-alignment-checks",
            new { extractionRevision = revision, sourceLanguage = language.SourceLanguage, targetLanguage = language.TargetLanguage }, cancellationToken);
    private async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl(path)) { Content = JsonContent.Create(body, options: ImportJson.Options) };
        using var response = await Api.SendAsync(request, cancellationToken);
        return await ReadResultAsync<T>(response, cancellationToken);
    }
}

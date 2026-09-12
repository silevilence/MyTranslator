using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>共享人工编辑 API 客户端；继承全局鉴权、地址与错误响应处理。</summary>
public sealed class SegmentEditingService(ApiClient api, IConfiguration configuration) : RunApiClientBase(api, configuration)
{
    public async Task<Segment> SaveAsync(Guid taskId, Guid segmentId, SaveSegmentRequest body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, ApiUrl($"/api/tasks/{taskId}/segments/{segmentId}"))
            { Content = JsonContent.Create(body, options: ImportJson.Options) };
        using var response = await Api.SendAsync(request, cancellationToken);
        return await ReadResultAsync<Segment>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<Segment>> ConfirmAsync(Guid taskId, ConfirmSegmentsRequest body, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl($"/api/tasks/{taskId}/segment-confirmations"))
            { Content = JsonContent.Create(body, options: ImportJson.Options) };
        using var response = await Api.SendAsync(request, cancellationToken);
        return await ReadResultAsync<Segment[]>(response, cancellationToken);
    }
}

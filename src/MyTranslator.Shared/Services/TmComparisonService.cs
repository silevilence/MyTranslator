using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 历史翻译对比接口客户端（docs/back/TM 接口约定.md §6.1）。
/// 调用方必须提交自己看到的提取修订与分段版本，服务端据此做乐观并发校验；
/// 响应形状见 <see cref="TmComparisonResponse"/>。
/// 接口地址经配置 <c>Api:BaseUrl</c> 注入；非成功响应统一抛出
/// <see cref="ApiErrorException"/>，由页面经 <see cref="ApiErrorMessageProvider"/> 映射文案。
/// </summary>
public class TmComparisonService
{
    private readonly ApiClient _api;
    private readonly string _baseUrl;

    public TmComparisonService(ApiClient api, IConfiguration config)
    {
        _api = api;
        _baseUrl = config["Api:BaseUrl"] ?? string.Empty;
    }

    /// <summary>拼接接口地址：配置了 <c>Api:BaseUrl</c> 时指向后端，否则使用页面同源相对路径。</summary>
    private string ApiUrl(string path) => _baseUrl + path;

    /// <summary>
    /// 任务分段历史翻译对比（§6.1）。后端读取指定分段当前已保存的源文、译文与标记表；
    /// <paramref name="extractionRevision"/> / <paramref name="segmentVersion"/> 为调用方最后读取的版本，
    /// 不匹配时分别返回 409 extraction_revision_changed / segment_version_conflict。
    /// </summary>
    /// <param name="limit">返回匹配数，默认 5，范围 1..20。</param>
    public async Task<TmComparisonResponse> GetSegmentComparisonAsync(
        Guid taskId,
        Guid segmentId,
        int extractionRevision,
        int segmentVersion,
        string sourceLanguage,
        string targetLanguage,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder()
            .Append("?extractionRevision=").Append(extractionRevision)
            .Append("&segmentVersion=").Append(segmentVersion)
            .Append("&sourceLanguage=").Append(Uri.EscapeDataString(sourceLanguage))
            .Append("&targetLanguage=").Append(Uri.EscapeDataString(targetLanguage))
            .Append("&limit=").Append(limit);

        using var response = await _api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/segments/{segmentId}/tm-comparison{query}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<TmComparisonResponse>(TmComparisonJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("接口响应缺少 TmComparisonResponse 内容");
    }
}

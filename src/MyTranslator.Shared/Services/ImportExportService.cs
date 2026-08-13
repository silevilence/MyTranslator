using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

/// <summary>
/// 文件导入拆解与导出接口客户端（docs/back/文件导入拆解与导出接口约定.md）。
/// 覆盖上传/URL 导入、读取任务摘要与分段、重新提取预览与应用、导出下载。
/// 接口地址经配置 <c>Api:BaseUrl</c> 注入（与健康检查一致）；非成功响应统一抛出
/// <see cref="ApiErrorException"/>，由页面经 <see cref="ApiErrorMessageProvider"/> 映射文案。
/// </summary>
public class ImportExportService
{
    private const string SegmentPageSize = "100";

    private readonly ApiClient _api;
    private readonly string _baseUrl;

    public ImportExportService(ApiClient api, IConfiguration config)
    {
        _api = api;
        _baseUrl = config["Api:BaseUrl"] ?? string.Empty;
    }

    /// <summary>拼接接口地址：配置了 <c>Api:BaseUrl</c> 时指向后端，否则使用页面同源相对路径。</summary>
    private string ApiUrl(string path) => _baseUrl + path;

    /// <summary>
    /// 上传文件创建导入任务（§4.1）。
    /// </summary>
    /// <param name="content">文件内容流（调用方负责释放）。</param>
    /// <param name="fileName">原始文件名（服务端用于推断类型与建议导出名）。</param>
    /// <param name="mediaType">上传媒体类型；为空时使用 application/octet-stream。</param>
    /// <param name="fileType">显式文件类型；null 表示由服务端推断。</param>
    /// <param name="segmentationMode">仅 txt 可用：`paragraph` / `line`；null 表示智能推荐。</param>
    public async Task<TaskSummary> CreateFromFileAsync(
        Stream content,
        string fileName,
        string? mediaType,
        string? fileType,
        string? segmentationMode,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType);
        form.Add(fileContent, "file", fileName);

        if (!string.IsNullOrWhiteSpace(fileType))
        {
            form.Add(new StringContent(fileType), "fileType");
        }

        if (!string.IsNullOrWhiteSpace(segmentationMode))
        {
            form.Add(new StringContent(segmentationMode), "segmentationMode");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl("/api/tasks/imports/file"))
        {
            Content = form,
        };
        return await SendForResultAsync<TaskSummary>(request, cancellationToken);
    }

    /// <summary>
    /// 从 URL 导入创建任务（§4.2）。URL 导入仅允许 html，始终显式携带 fileType=html。
    /// </summary>
    public async Task<TaskSummary> CreateFromUrlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { url, fileType = "html" }, ImportJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl("/api/tasks/imports/url"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return await SendForResultAsync<TaskSummary>(request, cancellationToken);
    }

    /// <summary>读取任务摘要（§4.4）。</summary>
    public async Task<TaskSummary> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync($"{ApiUrl("/api/tasks/")}{taskId}", cancellationToken);
        return await ReadResultAsync<TaskSummary>(response, cancellationToken);
    }

    /// <summary>分页读取可译分段（§6）。游标不透明，由服务端签发。</summary>
    public async Task<SegmentPage> GetSegmentsAsync(
        Guid taskId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var query = cursor is null
            ? $"?limit={SegmentPageSize}"
            : $"?limit={SegmentPageSize}&cursor={Uri.EscapeDataString(cursor)}";
        using var response = await _api.GetAsync($"{ApiUrl("/api/tasks/")}{taskId}/segments{query}", cancellationToken);
        return await ReadResultAsync<SegmentPage>(response, cancellationToken);
    }

    /// <summary>创建重新提取预览（§11.1）。</summary>
    public async Task<ReextractionPreview> CreateReextractionPreviewAsync(
        Guid taskId,
        string selector,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { selector }, ImportJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl("/api/tasks/")}{taskId}/extraction-previews")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return await SendForResultAsync<ReextractionPreview>(request, cancellationToken);
    }

    /// <summary>分页读取尚未过期的预览分段（§11.1）。</summary>
    public async Task<SegmentPage> GetReextractionPreviewSegmentsAsync(
        Guid taskId,
        Guid previewId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var query = cursor is null
            ? $"?limit={SegmentPageSize}"
            : $"?limit={SegmentPageSize}&cursor={Uri.EscapeDataString(cursor)}";
        using var response = await _api.GetAsync(
            $"{ApiUrl("/api/tasks/")}{taskId}/extraction-previews/{previewId}/segments{query}",
            cancellationToken);
        return await ReadResultAsync<SegmentPage>(response, cancellationToken);
    }

    /// <summary>
    /// 应用重新提取预览（§11.2）。
    /// <paramref name="confirmTranslationLoss"/> 为 true 表示用户已确认接受译文损失。
    /// 成功返回新的任务摘要（extractionRevision 已递增）。
    /// </summary>
    public async Task<TaskSummary> ApplyReextractionAsync(
        Guid taskId,
        Guid previewId,
        bool confirmTranslationLoss,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { confirmTranslationLoss }, ImportJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiUrl("/api/tasks/")}{taskId}/extraction-previews/{previewId}/apply")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return await SendForResultAsync<TaskSummary>(request, cancellationToken);
    }

    /// <summary>导出回填还原的文件（§10）。下载文件名取自响应 Content-Disposition。</summary>
    public async Task<ExportResult> ExportAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        using var response = await _api.GetAsync($"{ApiUrl("/api/tasks/")}{taskId}/export", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new ExportResult(
            content,
            ParseContentDispositionFileName(response.Content.Headers.ContentDisposition) ?? $"{taskId}.translated",
            mediaType);
    }

    private async Task<T> SendForResultAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _api.SendAsync(request, cancellationToken);
        return await ReadResultAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(ImportJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"接口响应缺少 {typeof(T).Name} 内容");
    }

    /// <summary>
    /// 解析 Content-Disposition 下载文件名：优先 RFC 5987 `filename*`（.NET 已解码），
    /// 回退 `filename` 并去除可能保留的引号。
    /// </summary>
    internal static string? ParseContentDispositionFileName(ContentDispositionHeaderValue? disposition)
    {
        if (disposition is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(disposition.FileNameStar))
        {
            return disposition.FileNameStar;
        }

        if (string.IsNullOrWhiteSpace(disposition.FileName))
        {
            return null;
        }

        var name = disposition.FileName.Trim();
        return name.Length >= 2 && name[0] == '"' && name[^1] == '"'
            ? name[1..^1]
            : name;
    }
}

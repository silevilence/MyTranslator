using System.Text;
using System.Text.Json.Nodes;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Translation;

/// <summary>在连接器序列化后写入服务原生关闭开关，确保实际线上请求包含 false。</summary>
internal sealed class IndexThinkingHandler(HttpClient upstream, string kind) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.Content ?? throw new InvalidOperationException("Missing chat request body.");
        var body = JsonNode.Parse(await original.ReadAsStringAsync(cancellationToken))!.AsObject();
        if (kind == AiProviderKinds.OpenAi)
        {
            var template = body["chat_template_kwargs"] as JsonObject ?? new JsonObject();
            template["enable_thinking"] = false;
            body["chat_template_kwargs"] = template;
            body.Remove("reasoning_effort");
        }
        else
        {
            body["think"] = false;
        }
        // HttpClient 不允许同一 HttpRequestMessage 被发送两次；保留连接器请求头并复制传输消息。
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        foreach (var header in request.Headers)
            forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options)
            forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        return await upstream.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) upstream.Dispose();
        base.Dispose(disposing);
    }
}

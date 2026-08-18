using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MyTranslator.Api.Tests;

internal abstract class TestTranslationChatClient : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var userMessage = messages.Last(message => message.Role == ChatRole.User).Text;
        using var document = JsonDocument.Parse(userMessage);
        var request = new TestTranslationRequest(
            document.RootElement.TryGetProperty("sourceLanguage", out var source) &&
            source.ValueKind == JsonValueKind.String
                ? source.GetString()
                : null,
            document.RootElement.GetProperty("targetLanguage").GetString()!,
            document.RootElement.GetProperty("segments").EnumerateArray()
                .Select(segment => new TestTranslationSegment(
                    segment.GetProperty("segmentId").GetGuid(),
                    segment.GetProperty("sourceText").GetString()!))
                .ToArray());
        var outputs = await TranslateCoreAsync(request, cancellationToken);
        var content = JsonSerializer.Serialize(new
        {
            translations = outputs.Select(output => new
            {
                segmentId = output.SegmentId,
                targetText = output.TargetText
            })
        });
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new NotSupportedException();
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    protected abstract Task<IReadOnlyList<TestTranslationOutput>> TranslateCoreAsync(
        TestTranslationRequest request,
        CancellationToken cancellationToken);
}

internal sealed record TestTranslationRequest(
    string? SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<TestTranslationSegment> Segments);

internal sealed record TestTranslationSegment(Guid SegmentId, string SourceText);

internal sealed record TestTranslationOutput(Guid SegmentId, string TargetText);

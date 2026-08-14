using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MyTranslator.Api.Translation;

public sealed class OpenAiCompatibleTranslationProvider(
    HttpClient httpClient,
    IOptions<TranslationOptions> options) : ITranslationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "openai-compatible";

    public async Task<IReadOnlyList<TranslationProviderOutput>> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(configuration.RequestTimeout);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{configuration.BaseUrl!.TrimEnd('/')}/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = configuration.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "Translate every segment and return JSON as {\"translations\":[{\"segmentId\":\"uuid\",\"targetText\":\"...\"}]}. Preserve every <xN>, </xN>, and <xN/> token exactly, including order and nesting."
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        sourceLanguage = request.SourceLanguage,
                        targetLanguage = request.TargetLanguage,
                        segments = request.Segments.Select(segment => new
                        {
                            segmentId = segment.SegmentId,
                            sourceText = segment.SourceText
                        })
                    }, JsonOptions)
                }
            }
        }, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException(
                "llm_provider_timeout",
                true,
                "The LLM request timed out.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationProviderException(
                "llm_provider_unavailable",
                true,
                "The LLM provider is unavailable.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await MapFailureAsync(response, timeout.Token);
            }

            try
            {
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(timeout.Token),
                    cancellationToken: timeout.Token);
                var content = document.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();
                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new JsonException("The LLM response content is empty.");
                }

                using var translationDocument = JsonDocument.Parse(StripCodeFence(content));
                return translationDocument.RootElement
                    .GetProperty("translations")
                    .EnumerateArray()
                    .Select(item => new TranslationProviderOutput(
                        item.GetProperty("segmentId").GetGuid(),
                        item.GetProperty("targetText").GetString() ?? string.Empty))
                    .ToArray();
            }
            catch (Exception exception) when (exception is
                JsonException or
                KeyNotFoundException or
                InvalidOperationException or
                FormatException or
                IndexOutOfRangeException or
                ArgumentOutOfRangeException)
            {
                throw new TranslationProviderException(
                    "llm_response_invalid",
                    true,
                    "The LLM returned an invalid structured response.",
                    exception);
            }
        }
    }

    private static async Task<TranslationProviderException> MapFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity &&
            await HasErrorCodeAsync(response.Content, "unsupported_language_pair", cancellationToken))
        {
            return new TranslationProviderException(
                "unsupported_language_pair",
                false,
                "The LLM provider does not support the requested language pair.");
        }

        return MapFailureStatus(response.StatusCode);
    }

    private static async Task<bool> HasErrorCodeAsync(
        HttpContent content,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            var codeElement = root.TryGetProperty("error", out var error) &&
                              error.ValueKind == JsonValueKind.Object &&
                              error.TryGetProperty("code", out var nestedCode)
                ? nestedCode.GetString()
                : root.TryGetProperty("code", out var topLevelCode)
                    ? topLevelCode.GetString()
                    : null;
            return string.Equals(codeElement, expectedCode, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static TranslationProviderException MapFailureStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new TranslationProviderException(
            "llm_authentication_failed",
            false,
            "The LLM provider rejected the configured credentials."),
        HttpStatusCode.NotFound => new TranslationProviderException(
            "llm_model_unavailable",
            false,
            "The configured LLM model is unavailable."),
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new TranslationProviderException(
            "llm_provider_timeout",
            true,
            "The LLM provider timed out."),
        HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => new TranslationProviderException(
            "llm_provider_unavailable",
            true,
            "The LLM provider is unavailable."),
        _ => new TranslationProviderException(
            "llm_response_invalid",
            false,
            "The LLM provider rejected the translation request.")
    };

    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? trimmed[(firstLineEnd + 1)..lastFence].Trim()
            : trimmed;
    }
}

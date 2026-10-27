using System.Net;
using Microsoft.Extensions.AI;
using MyTranslator.Api.Data;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Exceptions;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace MyTranslator.Api.Translation;

public sealed class AiChatClientFactory(IHttpClientFactory httpClientFactory) : IAiChatClientFactory
{
    public IChatClient Create(AiProvider provider, AiModel model) => provider.Kind switch
    {
        AiProviderKinds.OpenAi => CreateOpenAi(provider, model),
        AiProviderKinds.Ollama => CreateOllama(provider, model),
        _ => throw new NotSupportedException($"Unsupported AI provider kind '{provider.Kind}'.")
    };

    private HttpClient CreateHttpClient(AiProvider provider, AiModel model)
    {
        var client = httpClientFactory.CreateClient("AiChatClient");
        return model.TranslationMessageFormat == MyTranslator.Contracts.TranslationMessageFormats.IndexTranslate
            ? new HttpClient(new IndexThinkingHandler(client, provider.Kind)) { Timeout = Timeout.InfiniteTimeSpan }
            : client;
    }

    private IChatClient CreateOpenAi(AiProvider provider, AiModel model)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl!, UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(CreateHttpClient(provider, model)),
            RetryPolicy = new ClientRetryPolicy(0)
        };
        return new ChatClient(model.ModelId, new ApiKeyCredential(provider.ApiKey!), options).AsIChatClient();
    }

    private IChatClient CreateOllama(AiProvider provider, AiModel model)
    {
        var httpClient = CreateHttpClient(provider, model);
        httpClient.BaseAddress = new Uri(provider.BaseUrl!, UriKind.Absolute);
        return new ErrorMappedOllamaApiClient(httpClient, model.ModelId);
    }

    private sealed class ErrorMappedOllamaApiClient(HttpClient httpClient, string model)
        : OllamaApiClient(httpClient, model)
    {
        protected override async Task<HttpResponseMessage> SendToOllamaAsync(
            HttpRequestMessage requestMessage,
            OllamaRequest? ollamaRequest,
            HttpCompletionOption completionOption,
            CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendToOllamaAsync(
                    requestMessage,
                    ollamaRequest,
                    completionOption,
                    cancellationToken);
            }
            catch (OllamaException exception)
            {
                throw new HttpRequestException(
                    exception.Message,
                    exception,
                    HttpStatusCode.BadRequest);
            }
        }
    }
}

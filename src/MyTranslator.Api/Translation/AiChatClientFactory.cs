using Microsoft.Extensions.AI;
using MyTranslator.Api.Data;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace MyTranslator.Api.Translation;

public sealed class AiChatClientFactory(IHttpClientFactory httpClientFactory) : IAiChatClientFactory
{
    public IChatClient Create(AiProvider provider, AiModel model) => provider.Kind switch
    {
        "openai" => CreateOpenAi(provider, model),
        "ollama" => new OllamaChatClient(
            new Uri(provider.BaseUrl!, UriKind.Absolute),
            model.ModelId,
            httpClientFactory.CreateClient("AiChatClient")),
        _ => throw new NotSupportedException($"Unsupported AI provider kind '{provider.Kind}'.")
    };

    private IChatClient CreateOpenAi(AiProvider provider, AiModel model)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl!, UriKind.Absolute),
            Transport = new HttpClientPipelineTransport(httpClientFactory.CreateClient("AiChatClient")),
            RetryPolicy = new ClientRetryPolicy(0)
        };
        return new ChatClient(model.ModelId, new ApiKeyCredential(provider.ApiKey!), options).AsIChatClient();
    }
}

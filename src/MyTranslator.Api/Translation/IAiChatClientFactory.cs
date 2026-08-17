using Microsoft.Extensions.AI;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.Translation;

public interface IAiChatClientFactory
{
    IChatClient Create(AiProvider provider, AiModel model);
}

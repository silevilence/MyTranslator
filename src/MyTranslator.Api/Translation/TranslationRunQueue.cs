using System.Threading.Channels;

namespace MyTranslator.Api.Translation;

public sealed class TranslationRunQueue
{
    private readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    public void Enqueue(Guid runId)
    {
        if (!channel.Writer.TryWrite(runId))
        {
            throw new InvalidOperationException("The translation queue is unavailable.");
        }
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}

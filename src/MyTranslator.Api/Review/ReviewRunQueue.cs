using System.Threading.Channels;

namespace MyTranslator.Api.Review;

public sealed class ReviewRunQueue
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
            throw new InvalidOperationException("The review queue is unavailable.");
        }
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}

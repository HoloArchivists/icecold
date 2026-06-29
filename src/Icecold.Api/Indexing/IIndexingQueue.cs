using System.Threading.Channels;

namespace Icecold.Api.Indexing;

public interface IIndexingQueue
{
    ValueTask EnqueueAsync(Guid torrentId, CancellationToken cancellationToken);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}

public sealed class ChannelIndexingQueue : IIndexingQueue
{
    readonly Channel<Guid> channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(Guid torrentId, CancellationToken cancellationToken)
        => channel.Writer.WriteAsync(torrentId, cancellationToken);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken)
        => channel.Reader.ReadAllAsync(cancellationToken);
}

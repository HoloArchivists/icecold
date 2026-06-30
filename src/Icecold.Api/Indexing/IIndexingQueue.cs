using System.Threading.Channels;
using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Indexing;

public interface IIndexingQueue
{
    ValueTask EnqueueAsync(Guid torrentId, CancellationToken cancellationToken);

    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}

public sealed class ChannelIndexingQueue(IOptions<IcecoldOptions> options) : IIndexingQueue
{
    readonly Channel<Guid> channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(
        Math.Max(1, options.Value.Indexing.QueueCapacity))
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    public ValueTask EnqueueAsync(Guid torrentId, CancellationToken cancellationToken)
        => channel.Writer.WriteAsync(torrentId, cancellationToken);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken)
        => channel.Reader.ReadAllAsync(cancellationToken);
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.Indexing;

public interface IIndexingQueue
{
    // Never blocks. Returns false when the in-memory queue is full; the row stays Pending in
    // the database and the worker's periodic pending sweep enqueues it later.
    bool TryEnqueue(Guid torrentId);

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

    // Ids currently buffered in the channel, so repeated sweeps do not fill it with duplicates.
    readonly ConcurrentDictionary<Guid, byte> queued = new();

    public bool TryEnqueue(Guid torrentId)
    {
        if (!queued.TryAdd(torrentId, 0))
            return true;

        if (channel.Writer.TryWrite(torrentId))
            return true;

        queued.TryRemove(torrentId, out _);
        return false;
    }

    public async IAsyncEnumerable<Guid> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var torrentId in channel.Reader.ReadAllAsync(cancellationToken))
        {
            queued.TryRemove(torrentId, out _);
            yield return torrentId;
        }
    }
}

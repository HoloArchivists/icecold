using Icecold.Api.Indexing;
using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class IndexingQueueTests
{
    [Fact]
    public async Task TryEnqueue_Does_Not_Block_When_Bounded_Queue_Is_Full()
    {
        var queue = CreateQueue(capacity: 1);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));

        await using var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(first, reader.Current);

        Assert.True(queue.TryEnqueue(second));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(second, reader.Current);
    }

    [Fact]
    public async Task TryEnqueue_Deduplicates_Ids_Still_In_The_Queue()
    {
        var queue = CreateQueue(capacity: 2);
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();

        Assert.True(queue.TryEnqueue(id));
        Assert.True(queue.TryEnqueue(id));
        Assert.True(queue.TryEnqueue(other));

        await using var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(id, reader.Current);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(other, reader.Current);

        // Once dequeued, the same id can be queued again (e.g. a retry).
        Assert.True(queue.TryEnqueue(id));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(id, reader.Current);
    }

    static ChannelIndexingQueue CreateQueue(int capacity)
        => new(Options.Create(new IcecoldOptions
        {
            Indexing = new IndexingOptions { QueueCapacity = capacity }
        }));
}

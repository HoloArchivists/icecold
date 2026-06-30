using Icecold.Api.Indexing;
using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class IndexingQueueTests
{
    [Fact]
    public async Task EnqueueAsync_Waits_When_Bounded_Queue_Is_Full()
    {
        var queue = new ChannelIndexingQueue(Options.Create(new IcecoldOptions
        {
            Indexing = new IndexingOptions { QueueCapacity = 1 }
        }));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await queue.EnqueueAsync(first, CancellationToken.None);

        var secondWrite = queue.EnqueueAsync(second, CancellationToken.None).AsTask();
        Assert.NotSame(secondWrite, await Task.WhenAny(secondWrite, Task.Delay(100)));

        await using var reader = queue.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(first, reader.Current);

        await secondWrite.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(second, reader.Current);
    }
}

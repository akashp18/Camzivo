using Camzivo.Core.Streaming;
using Xunit;

namespace Camzivo.Core.Tests;

public class BoundedFrameQueueTests
{
    [Fact]
    public void CapacityMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedFrameQueue<int>(0));
    }

    [Fact]
    public void OverflowDropsTheOldestFrameAndCountsIt()
    {
        var queue = new BoundedFrameQueue<int>(3);

        queue.TryEnqueue(1);
        queue.TryEnqueue(2);
        queue.TryEnqueue(3);
        var dropped = queue.TryEnqueue(4);

        Assert.True(dropped);
        Assert.Equal(3, queue.Count);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(2, first);

        var stats = queue.Statistics;
        Assert.Equal(1, stats.Dropped);
        Assert.Equal(4, stats.Enqueued);
        Assert.Equal(1, stats.Dequeued);
    }

    [Fact]
    public void QueueNeverGrowsPastCapacity()
    {
        var queue = new BoundedFrameQueue<int>(4);

        for (var i = 0; i < 10_000; i++)
        {
            queue.TryEnqueue(i);
        }

        Assert.Equal(4, queue.Count);
        Assert.Equal(9_996, queue.Statistics.Dropped);
    }

    [Fact]
    public void DequeueOnEmptyReturnsFalse()
    {
        var queue = new BoundedFrameQueue<string>(2);

        Assert.False(queue.TryDequeue(out var item));
        Assert.Null(item);
    }

    [Fact]
    public void ClearEmptiesTheQueueWithoutRewindingLifetimeCounters()
    {
        var queue = new BoundedFrameQueue<int>(2);
        queue.TryEnqueue(1);
        queue.TryEnqueue(2);

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.Equal(2, queue.Statistics.Enqueued);
    }

    [Fact]
    public async Task ConcurrentProducerAndConsumerKeepTheAccountingInvariant()
    {
        const int total = 50_000;
        var queue = new BoundedFrameQueue<int>(8);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < total && !cancellation.IsCancellationRequested; i++)
            {
                queue.TryEnqueue(i);
            }
        });

        var consumer = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (queue.TryDequeue(out _))
                {
                    continue;
                }

                if (producer.IsCompleted)
                {
                    break;
                }

                Thread.SpinWait(64);
            }
        });

        await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(15));

        // Throughput is timing-dependent; the invariant is not. Every frame is either consumed, dropped or
        // still queued, and the queue never grew past its capacity under contention.
        var stats = queue.Statistics;
        Assert.Equal(total, stats.Enqueued);
        Assert.Equal(stats.Enqueued - stats.Dequeued - stats.Dropped, stats.Count);
        Assert.True(stats.Count <= 8);
    }
}

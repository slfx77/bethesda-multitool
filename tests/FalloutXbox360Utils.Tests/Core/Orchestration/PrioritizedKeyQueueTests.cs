using FalloutXbox360Utils.Core.Orchestration;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Orchestration;

public sealed class PrioritizedKeyQueueTests
{
    [Fact]
    public void Lower_priority_dequeues_first()
    {
        var queue = new PrioritizedKeyQueue<string>();
        queue.Enqueue("far", 100f);
        queue.Enqueue("near", 1f);
        queue.Enqueue("mid", 50f);

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.True(queue.TryDequeue(out var third));
        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(["near", "mid", "far"], new[] { first, second, third });
    }

    [Fact]
    public void Equal_priorities_dequeue_in_strict_fifo_order()
    {
        var queue = new PrioritizedKeyQueue<int>();
        for (var i = 0; i < 100; i++)
        {
            queue.Enqueue(i);
        }

        for (var i = 0; i < 100; i++)
        {
            Assert.True(queue.TryDequeue(out var key));
            Assert.Equal(i, key);
        }
    }

    [Fact]
    public void Duplicate_keys_are_rejected_and_the_first_priority_wins()
    {
        var queue = new PrioritizedKeyQueue<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(queue.Enqueue("Key", 50f));
        Assert.False(queue.Enqueue("key", 1f)); // would jump the queue if honored

        queue.Enqueue("other", 10f);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("other", first);
    }

    [Fact]
    public void Dequeued_keys_can_be_re_enqueued()
    {
        var queue = new PrioritizedKeyQueue<string>();
        queue.Enqueue("k", 1f);
        Assert.True(queue.TryDequeue(out _));
        Assert.True(queue.Enqueue("k", 2f));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Contains_and_Clear_track_the_queued_set()
    {
        var queue = new PrioritizedKeyQueue<string>();
        queue.Enqueue("k", 1f);
        Assert.True(queue.Contains("k"));

        queue.Clear();
        Assert.False(queue.Contains("k"));
        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _));
    }
}
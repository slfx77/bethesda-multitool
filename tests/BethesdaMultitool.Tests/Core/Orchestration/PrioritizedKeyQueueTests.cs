using BethesdaMultitool.Core.Orchestration;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Orchestration;

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
    public void Duplicate_keys_are_not_re_added_but_a_lower_priority_promotes()
    {
        var queue = new PrioritizedKeyQueue<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(queue.Enqueue("Key", 50f));
        // Not NEWLY enqueued (returns false), but the nearer priority is honored — the camera
        // approaching a far-sighted mesh must not leave it parked behind the far-field backlog.
        Assert.False(queue.Enqueue("key", 1f));
        Assert.Equal(1, queue.Count);

        queue.Enqueue("other", 10f);
        Assert.True(queue.TryDequeue(out var first));
        // The promoted entry carries the spelling passed at promote time — equal under the comparer.
        Assert.Equal("key", first, ignoreCase: true);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("other", second);
        // The superseded 50f entry is a stale duplicate — it must not dequeue "Key" a second time.
        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Re_enqueueing_with_a_higher_priority_never_demotes()
    {
        var queue = new PrioritizedKeyQueue<string>();
        Assert.True(queue.Enqueue("k", 1f));
        Assert.False(queue.Enqueue("k", 100f));

        queue.Enqueue("mid", 10f);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("k", first);
    }

    [Fact]
    public void Key_re_enqueued_after_dequeue_is_not_shadowed_by_its_stale_entry()
    {
        var queue = new PrioritizedKeyQueue<string>();
        queue.Enqueue("k", 50f);
        queue.Enqueue("k", 1f);           // promote — leaves a stale 50f heap entry behind
        Assert.True(queue.TryDequeue(out _)); // consumes the live 1f entry

        // Fresh membership generation for the same key; the leftover 50f entry must be skipped
        // (not mistaken for the live one) and the new 25f entry must dequeue exactly once.
        Assert.True(queue.Enqueue("k", 25f));
        Assert.True(queue.TryDequeue(out var key));
        Assert.Equal("k", key);
        Assert.False(queue.TryDequeue(out _));
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
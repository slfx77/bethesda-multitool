using System;
using System.Threading;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Device-free coverage of the uploader-thread dispatcher (the threading/back-pressure half
///     of the async copy-queue upload path). The D3D12 copy queue itself (<c>GpuUploadQueue12</c>)
///     needs a real device, so its fence/state correctness is validated via the renderer profiler
///     + D3D12 debug layer rather than here.
/// </summary>
public sealed class GpuUploadDispatcher12Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void EnqueuedWork_RunsOnABackgroundThread()
    {
        using var dispatcher = new GpuUploadDispatcher12();
        var callerThreadId = Environment.CurrentManagedThreadId;
        var workThreadId = 0;
        using var done = new ManualResetEventSlim(false);

        Assert.True(dispatcher.TryEnqueue(() =>
        {
            workThreadId = Environment.CurrentManagedThreadId;
            done.Set();
        }));

        Assert.True(done.Wait(Timeout));
        Assert.NotEqual(callerThreadId, workThreadId);
    }

    [Fact]
    public void AllEnqueuedWork_Executes()
    {
        using var dispatcher = new GpuUploadDispatcher12();
        const int count = 50;
        using var countdown = new CountdownEvent(count);

        for (var i = 0; i < count; i++)
        {
            Assert.True(dispatcher.TryEnqueue(() => countdown.Signal()));
        }

        Assert.True(countdown.Wait(Timeout));
    }

    [Fact]
    public void Stop_DrainsRemainingWork_BeforeReturning()
    {
        var dispatcher = new GpuUploadDispatcher12();
        var ran = 0;
        for (var i = 0; i < 20; i++)
        {
            Assert.True(dispatcher.TryEnqueue(() => Interlocked.Increment(ref ran)));
        }

        dispatcher.Stop();

        // CompleteAdding + GetConsumingEnumerable drains every buffered item before the thread
        // exits, and Stop joins it — so all queued work has run by the time Stop returns.
        Assert.Equal(20, Volatile.Read(ref ran));
        dispatcher.Dispose();
    }

    [Fact]
    public void TryEnqueue_ReturnsFalse_AfterStop()
    {
        var dispatcher = new GpuUploadDispatcher12();
        dispatcher.Stop();

        Assert.False(dispatcher.TryEnqueue(static () => { }));
        dispatcher.Dispose();
    }

    [Fact]
    public void TryEnqueue_ReturnsFalse_WhenQueueFull_WithoutBlocking()
    {
        using var dispatcher = new GpuUploadDispatcher12(capacity: 2);
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        // Block the single uploader thread inside the first item so it stops draining the queue.
        Assert.True(dispatcher.TryEnqueue(() =>
        {
            started.Set();
            release.Wait(Timeout);
        }));
        Assert.True(started.Wait(Timeout));

        // Consumer is parked → exactly `capacity` items fit, then the next add fails immediately.
        Assert.True(dispatcher.TryEnqueue(static () => { }));
        Assert.True(dispatcher.TryEnqueue(static () => { }));
        Assert.False(dispatcher.TryEnqueue(static () => { }));

        release.Set();
    }

    [Fact]
    public void WorkItemException_DoesNotKillTheUploaderThread()
    {
        using var dispatcher = new GpuUploadDispatcher12();
        using var done = new ManualResetEventSlim(false);

        Assert.True(dispatcher.TryEnqueue(static () => throw new InvalidOperationException("boom")));
        Assert.True(dispatcher.TryEnqueue(() => done.Set()));

        // The second item still runs, proving the throwing item didn't tear down the thread.
        Assert.True(done.Wait(Timeout));
    }
}

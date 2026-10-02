using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class SynchronizedLazyDisposableTests
{
    [Fact]
    public async Task Dispose_WaitsForActiveUse_ThenDisposesExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var useStarted = new ManualResetEventSlim();
        using var releaseUse = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        var owned = new ProbeDisposable();
        var factoryCalls = 0;
        var resource = new SynchronizedLazyDisposable<ProbeDisposable>(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return owned;
        });

        // Both operations deliberately block; dedicated workers keep their rendezvous independent
        // of thread-pool availability while the rest of the suite runs concurrently.
        var useTask = Task.Factory.StartNew(() => resource.Use(value =>
        {
            useStarted.Set();
            Assert.True(releaseUse.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.False(value.IsDisposed);
            return 42;
        }), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Task? disposeTask = null;
        try
        {
            Assert.True(useStarted.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            disposeTask = Task.Factory.StartNew(() =>
            {
                disposeStarted.Set();
                resource.Dispose();
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.False(disposeTask.IsCompleted);

            releaseUse.Set();
            Assert.Equal(42, await useTask);
            await disposeTask;

            Assert.Equal(1, factoryCalls);
            Assert.Equal(1, owned.DisposeCalls);
            Assert.Throws<ObjectDisposedException>(() => resource.Use(static _ => 0));

            resource.Dispose();
            Assert.Equal(1, owned.DisposeCalls);
        }
        finally
        {
            // Release and drain workers before their wait handles leave scope, including when
            // the test is canceled or a rendezvous assertion fails.
            releaseUse.Set();
            try
            {
                await Task.WhenAll(useTask, disposeTask ?? Task.CompletedTask);
            }
            finally
            {
                resource.Dispose();
            }
        }
    }

    [Fact]
    public void Dispose_BeforeFirstUse_DoesNotCreateResource()
    {
        var factoryCalls = 0;
        var resource = new SynchronizedLazyDisposable<ProbeDisposable>(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return new ProbeDisposable();
        });

        resource.Dispose();

        Assert.Equal(0, factoryCalls);
        Assert.Throws<ObjectDisposedException>(() => resource.Use(static _ => 0));
    }

    private sealed class ProbeDisposable : IDisposable
    {
        private int _disposeCalls;

        internal bool IsDisposed => Volatile.Read(ref _disposeCalls) != 0;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
        }
    }
}

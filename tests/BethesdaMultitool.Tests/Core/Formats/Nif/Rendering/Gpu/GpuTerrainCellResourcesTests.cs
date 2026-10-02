using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises the production terrain range owner with actual byte allocations and Shared retirement ownership.</summary>
public sealed class GpuTerrainCellResourcesTests
{
    /// <summary>Producer failure after transfer keeps the exact range until retirement and permanently closes acquisition.</summary>
    [Fact]
    public void FailedUploadRetainsTransferredRangeAndRejectsEscapedCallback()
    {
        var arena = new ByteArenaAllocator(256);
        var expected = Allocate(arena, "failed upload");
        var releases = 0;
        using var resources = new GpuTerrainCellResources12(geometry =>
        {
            Assert.Equal(expected, geometry);
            releases++;
            arena.Free(geometry.Allocation);
        });
        Action<TerrainAllocation12>? escaped = null;
        var failure = new IOException("Upload failed after a copy could have been recorded.");
        Assert.Same(failure, Assert.Throws<IOException>(() => resources.AcquireGeometry(retain =>
        {
            escaped = retain;
            retain(expected);
            throw failure;
        })));
        Assert.True(resources.HasGeometry);
        Assert.False(resources.IsReleased);
        Assert.Equal(expected.Allocation.AlignedSize, arena.AllocatedBytes);
        Assert.Equal(0, releases);
        Assert.Throws<InvalidOperationException>(() => resources.Geometry);
        Assert.Throws<InvalidOperationException>(() => escaped!(expected));
        var invokedAgain = false;
        Assert.Throws<InvalidOperationException>(() => resources.AcquireGeometry(_ =>
        {
            invokedAgain = true;
            return expected;
        }));
        Assert.False(invokedAgain);
        resources.Dispose();
        Assert.False(resources.HasGeometry);
        Assert.True(resources.IsReleased);
        Assert.Equal(0L, arena.AllocatedBytes);
        Assert.Throws<InvalidOperationException>(() => escaped!(expected));
        resources.Dispose();
        Assert.Equal(1, releases);
    }

    /// <summary>Failure or missing transfer cannot invent geometry ownership or permit a second acquisition attempt.</summary>
    /// <param name="producerThrows">Whether the producer fails or returns without calling its retention callback.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntransferredFailureIsOneShotAndNeverCallsArenaRelease(bool producerThrows)
    {
        var releases = 0;
        using var resources = new GpuTerrainCellResources12(_ => releases++);
        Assert.Throws<InvalidOperationException>(() => resources.AcquireGeometry(_ =>
        {
            if (producerThrows) throw new InvalidOperationException("No range was acquired.");
            return default;
        }));
        Assert.False(resources.HasGeometry);
        Assert.Throws<InvalidOperationException>(() => resources.AcquireGeometry(_ => default));
        Assert.Throws<InvalidOperationException>(() => resources.Geometry);
        resources.Dispose();
        resources.Dispose();
        Assert.True(resources.IsReleased);
        Assert.Equal(0, releases);
    }

    /// <summary>Matching coordinates from another allocator cannot replace the exact retained token after upload.</summary>
    [Fact]
    public void MismatchedCompletionRetainsOriginalAllocationOnly()
    {
        var firstArena = new ByteArenaAllocator(256);
        var secondArena = new ByteArenaAllocator(256);
        var retained = Allocate(firstArena, "first allocator");
        var foreign = Allocate(secondArena, "second allocator");
        Assert.Equal(retained.Allocation.Offset, foreign.Allocation.Offset);
        Assert.Equal(retained.Allocation.AllocationId, foreign.Allocation.AllocationId);
        Assert.NotEqual(retained.Allocation, foreign.Allocation);
        using var resources = new GpuTerrainCellResources12(geometry =>
        {
            Assert.Equal(retained, geometry);
            firstArena.Free(geometry.Allocation);
        });
        try
        {
            Assert.Throws<InvalidOperationException>(() => resources.AcquireGeometry(retain =>
            {
                retain(retained);
                return foreign;
            }));
            Assert.True(resources.HasGeometry);
            Assert.Throws<InvalidOperationException>(() => resources.Geometry);
            resources.Dispose();
            Assert.Equal(0L, firstArena.AllocatedBytes);
            Assert.Equal(foreign.Allocation.AlignedSize, secondArena.AllocatedBytes);
        }
        finally { secondArena.Free(foreign.Allocation); }
    }

    /// <summary>Only one synchronous transfer succeeds; wrong-thread calls and callback reentry cannot mutate ownership.</summary>
    [Fact]
    public void CallbackAndLifetimeGuardsPreserveExactCompletedViews()
    {
        var arena = new ByteArenaAllocator(256);
        var retained = Allocate(arena, "guarded acquisition");
        var completed = retained with { VertexGpuAddress = 0x1000, BlendGpuAddress = 0x1040 };
        GpuTerrainCellResources12? resources = null;
        resources = new GpuTerrainCellResources12(geometry =>
        {
            Assert.Equal(completed, geometry);
            Assert.Throws<InvalidOperationException>(resources!.Dispose);
            Assert.Throws<InvalidOperationException>(() => resources.AcquireGeometry(_ => retained));
            arena.Free(geometry.Allocation);
        });
        using (resources)
        {
            Action<TerrainAllocation12>? escaped = null;
            Assert.Equal(completed, resources.AcquireGeometry(retain =>
            {
                escaped = retain;
                AssertForeignThreadRejected(() => retain(retained));
                Assert.False(resources.HasGeometry);
                Assert.Throws<InvalidOperationException>(resources.Dispose);
                Assert.Throws<InvalidOperationException>(() => resources.AcquireGeometry(_ => retained));
                retain(retained);
                Assert.Throws<InvalidOperationException>(() => retain(retained));
                return completed;
            }));
            Assert.Equal(completed, resources.Geometry);
            AssertForeignThreadRejected(resources.Dispose,
                () => resources.AcquireGeometry(_ => retained), () => { _ = resources.Geometry; });
            Assert.True(resources.HasGeometry);
            Assert.Throws<InvalidOperationException>(() => escaped!(retained));
            resources.Dispose();
            Assert.True(resources.IsReleased);
            Assert.Equal(0L, arena.AllocatedBytes);
            Assert.Throws<ObjectDisposedException>(() => resources.Geometry);
            AssertForeignThreadRejected(resources.Dispose);
        }
    }

    /// <summary>Shared residency holds the range through pins and preserves its full charge while arena release fails.</summary>
    [Fact]
    public void ResidencyAndFailedReleaseRetainRangeUntilSuccessfulRetry()
    {
        var arena = new ByteArenaAllocator(256);
        var allocation = Allocate(arena, "residency retry");
        var charged = allocation.Allocation.AlignedSize;
        using var residency = new ResourceResidencyCache<string, GpuTerrainCellResources12>(charged, 1);
        var entry = residency.Reserve("cell", charged);
        var releases = 0;
        var failure = new IOException("Arena release is temporarily unavailable.");
        var resources = new GpuTerrainCellResources12(geometry =>
        {
            Assert.Equal(allocation, geometry);
            if (++releases == 1) throw failure;
            arena.Free(geometry.Allocation);
        });
        entry.Attach(resources);
        resources.AcquireGeometry(retain => { retain(allocation); return allocation; });
        entry.MarkPrepared();
        entry.PublishPrepared();
        Assert.True(residency.TryAcquire("cell", out var pin));
        using (pin)
        {
            Assert.True(residency.Remove("cell"));
            Assert.Throws<InvalidOperationException>(entry.ReleaseAfterRetirement);
            Assert.Equal(0, releases);
            Assert.Equal(allocation, pin!.Resource.Geometry);
        }
        var cleanupFailure = Assert.Throws<AggregateException>(entry.ReleaseAfterRetirement);
        Assert.Contains(failure, cleanupFailure.Flatten().InnerExceptions);
        Assert.True(resources.HasGeometry);
        Assert.False(resources.IsReleased);
        Assert.Equal(charged, arena.AllocatedBytes);
        Assert.Equal(charged, residency.TotalBytes);
        Assert.Throws<ObjectDisposedException>(() => resources.Geometry);
        entry.ReleaseAfterRetirement();
        Assert.True(resources.IsReleased);
        Assert.False(resources.HasGeometry);
        Assert.Equal(0L, arena.AllocatedBytes);
        Assert.Equal(0L, residency.TotalBytes);
        Assert.Equal(ResourceResidencyState.Released, entry.State);
        resources.Dispose();
        Assert.Equal(2, releases);
    }

    /// <summary>Creates a real allocation identity without creating a native buffer or submission.</summary>
    /// <param name="arena">Exact byte arena retaining the logical allocation.</param>
    /// <param name="tag">Diagnostic label carried through completed geometry views.</param>
    /// <returns>A retained eighty-byte range with uninitialized native addresses.</returns>
    private static TerrainAllocation12 Allocate(ByteArenaAllocator arena, string tag) =>
        new(arena.Allocate(80), 0, 0, 64, 16, tag);

    /// <summary>Runs guarded transitions on a bounded worker and reports every failure on the test thread.</summary>
    /// <param name="operations">Owner-thread-only operations that must fail before ownership changes.</param>
    private static void AssertForeignThreadRejected(params Action[] operations)
    {
        var failures = new Exception?[operations.Length];
        var worker = new Thread(() =>
        {
            for (var index = 0; index < operations.Length; index++)
                failures[index] = Record.Exception(operations[index]);
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "The terrain ownership checks did not finish.");
        foreach (var failure in failures) Assert.IsType<InvalidOperationException>(failure);
    }
}

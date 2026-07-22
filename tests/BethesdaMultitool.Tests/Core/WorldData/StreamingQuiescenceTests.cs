using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Locks the shared streaming-quiescence predicate (<see cref="StreamingQuiescence" />) that all
///     capture/export gates delegate to: which counters gate loose vs strict mode, which are
///     deliberately ignored (permanently-missing assets must never deadlock a gate), and the null =
///     layer-not-rendered convention.
/// </summary>
public sealed class StreamingQuiescenceTests
{
    private static WorldRenderStats Quiet() => new();

    [Fact]
    public void QuietStats_AreQuiescedInBothModes()
    {
        Assert.True(StreamingQuiescence.IsQuiesced(Quiet(), Quiet(), strict: false));
        Assert.True(StreamingQuiescence.IsQuiesced(Quiet(), Quiet(), strict: true));
    }

    [Theory]
    [InlineData("uploads")]
    [InlineData("queuedDecodes")]
    [InlineData("activeDecodes")]
    [InlineData("pendingResolves")]
    [InlineData("pendingUploads")]
    public void AnyActiveReferenceWork_BreaksBothModes(string counter)
    {
        var r = Quiet();
        switch (counter)
        {
            case "uploads": r.ReferenceGpuUploads = 1; break;
            case "queuedDecodes": r.ReferenceQueuedDecodes = 1; break;
            case "activeDecodes": r.ReferenceActiveDecodes = 1; break;
            case "pendingResolves": r.ReferenceTexturePendingResolves = 1; break;
            case "pendingUploads": r.ReferenceTexturePendingUploads = 1; break;
        }
        Assert.False(StreamingQuiescence.IsQuiesced(r, null, strict: false));
        Assert.False(StreamingQuiescence.IsQuiesced(r, null, strict: true));
    }

    [Fact]
    public void TexturePending_BreaksOnlyStrictMode()
    {
        // The texture-withheld window (resolved texture's async copy-queue upload hasn't flipped
        // TexturesReady): exports must wait it out, live convergence loops must NOT (it never
        // reaches zero in regions with permanently-missing textures).
        var r = Quiet();
        r.ReferenceTexturePending = 3;
        Assert.True(StreamingQuiescence.IsQuiesced(r, null, strict: false));
        Assert.False(StreamingQuiescence.IsQuiesced(r, null, strict: true));
    }

    [Fact]
    public void MeshMissing_NeverGates()
    {
        // 10-28k permanently-unresolvable meshes at dense spots — any gate on this deadlocks.
        var r = Quiet();
        r.ReferenceMeshMissing = 28_000;
        Assert.True(StreamingQuiescence.IsQuiesced(r, null, strict: false));
        Assert.True(StreamingQuiescence.IsQuiesced(r, null, strict: true));
    }

    [Fact]
    public void TerrainUploads_GateWhenProvided_SkippedWhenNull()
    {
        var t = Quiet();
        t.NewUploads = 2;
        Assert.False(StreamingQuiescence.IsQuiesced(Quiet(), t, strict: false));
        // Null = the layer wasn't rendered this frame (stale stats) — imposes no requirement.
        Assert.True(StreamingQuiescence.IsQuiesced(Quiet(), null, strict: false));
    }

    [Fact]
    public void NullReferences_ImposeNoRequirement()
    {
        // Layer-not-rendered convention: a terrain-only export must be able to settle.
        var t = Quiet();
        Assert.True(StreamingQuiescence.IsQuiesced(null, t, strict: true));
        Assert.True(StreamingQuiescence.IsQuiesced(null, null, strict: true));
    }

    [Fact]
    public async Task PollAsync_ImmediatelyQuiesced_ReturnsTrueWithoutDelay()
    {
        var calls = 0;
        var settled = await StreamingQuiescence.PollAsync(
            () => { calls++; return true; },
            timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);
        Assert.True(settled);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PollAsync_NeverQuiesced_TimesOutFalse()
    {
        var settled = await StreamingQuiescence.PollAsync(
            () => false,
            timeout: TimeSpan.FromMilliseconds(50), interval: TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);
        Assert.False(settled);
    }

    [Fact]
    public async Task PollAsync_QuiescesMidway_ReturnsTrue()
    {
        var calls = 0;
        var settled = await StreamingQuiescence.PollAsync(
            () => ++calls >= 3,
            timeout: TimeSpan.FromSeconds(5), interval: TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);
        Assert.True(settled);
    }
}

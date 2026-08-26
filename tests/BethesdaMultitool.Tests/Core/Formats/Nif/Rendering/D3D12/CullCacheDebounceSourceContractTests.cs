using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pins the sampling ORDER of the cull-cache streaming debounce in
///     <c>ReferenceRenderer12.Render</c>. A source-contract pin because the method needs a live
///     D3D12 device and cannot run headless — the case the repo's rules allow this shape for.
///     <para>
///         The bug being pinned against, measured 2026-08-25: <c>streamingDrained</c> was computed
///         from <c>LastStats.ReferenceQueuedDecodes / ReferenceActiveDecodes / ReferenceGpuUploads</c>
///         partway through <c>Render</c> — but <c>LastStats.Reset()</c> zeroes those at the top of
///         the method and they are not reassigned until after the batch pass. So all three read 0
///         unconditionally, <c>streamingDrained</c> was permanently <c>true</c>, and the
///         <c>!streamingDrained</c> clause deleted the debounce entirely. Result on FO76 Appalachia:
///         a <c>MeshBounds</c> cull-cache veto on 99.6% of 529 frames, a full batch rebuild every
///         frame, and 55.9 ms — 64% of the frame — in the pass the debounce exists to skip.
///     </para>
///     <para>
///         It stayed hidden for weeks because the diagnostic that names the vetoing clause was
///         itself dropped by <c>WorldRenderStats.Snapshot()</c> and never traced, and because two
///         guessed fixes were aimed at the downstream <c>ResolveReuseBlocker</c> quiescence gate
///         instead of this one.
///     </para>
/// </summary>
public sealed class CullCacheDebounceSourceContractTests
{
    private static string RendererSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
    }

    [Fact]
    public void Streaming_depths_are_sampled_before_LastStats_is_reset()
    {
        // The only frame that can answer "was streaming in flight?" is the previous one: this
        // frame's counters are not written until the end of Render. Sampling must therefore precede
        // the Reset that clears them.
        SourceContract.AssertOrder(
            RendererSource(),
            "var streamingInFlightLastFrame",
            "LastStats.Reset();");
    }

    [Fact]
    public void The_debounce_does_not_read_the_counters_it_just_cleared()
    {
        var source = RendererSource();

        // The exact regression shape: deriving the drain state from LastStats after Reset().
        Assert.DoesNotContain("streamingDrained = LastStats.", source, StringComparison.Ordinal);

        // And the clause still has to be wired to the sampled value, or the pin above would pass
        // while the debounce sat disconnected.
        Assert.Contains("var streamingDrained = !streamingInFlightLastFrame;", source, StringComparison.Ordinal);
        Assert.Contains("!streamingDrained", source, StringComparison.Ordinal);
    }
}

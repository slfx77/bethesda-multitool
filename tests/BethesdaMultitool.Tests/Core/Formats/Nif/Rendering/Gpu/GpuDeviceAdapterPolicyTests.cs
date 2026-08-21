using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Guards the Windows 10 / whole-12.x-family support contract: device creation walks
///     12_2 → 12_1 → 12_0 across every hardware adapter (not just the default one), and the
///     viewer's WARP fallback path produces a working, visibly-flagged software device.
/// </summary>
public sealed class GpuDeviceAdapterPolicyTests
{
    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void HardwareDeviceCreatesAtTwelveZeroOrHigher()
    {
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create();
        Assert.NotNull(device);
        Assert.True(device.FeatureLevel >= FeatureLevel.Level_12_0);
        Assert.False(device.IsSoftwareAdapter);
    }

    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void WarpOnlyPolicyYieldsAFlaggedSoftwareDevice()
    {
        GpuTestGuard.SkipUnlessEnabled();

        // WARP ships with every supported Windows 10+ build, so this must succeed even on
        // machines whose hardware path also works — it exercises exactly the fallback a
        // no-12_0-GPU user hits.
        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);
        Assert.True(device.FeatureLevel >= FeatureLevel.Level_12_0);
        Assert.True(device.IsSoftwareAdapter);
    }

    [Fact]
    public void ViewerRequestsWarpFallbackAndSurfacesTheOutcome()
    {
        // Source contract: the 3D view opts into the WARP fallback (a blank panel is never the
        // answer) and tells the user when it engaged; the CLI sprite path keeps its own CPU
        // rasterizer fallback instead of silently rendering sprites on WARP.
        var deviceInit = SourceContract.ReadAppSource("WorldView3DControl.Device.cs");
        Assert.Contains("GpuAdapterPolicy.PreferHardwareThenWarp", deviceInit, StringComparison.Ordinal);

        var lifecycle = SourceContract.ReadAppSource("WorldView3DControl.Lifecycle.cs");
        Assert.Contains("IsSoftwareAdapter", lifecycle, StringComparison.Ordinal);

        var cliSelector = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "SpriteRenderBackendSelector.cs");
        Assert.DoesNotContain("PreferHardwareThenWarp", cliSelector, StringComparison.Ordinal);
        Assert.DoesNotContain("WarpOnly", cliSelector, StringComparison.Ordinal);
    }
}

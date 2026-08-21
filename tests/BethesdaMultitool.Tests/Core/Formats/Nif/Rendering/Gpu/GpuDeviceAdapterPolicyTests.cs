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
    public void RootSignatureDoesNotDemandShaderModel66DynamicResources()
    {
        // REGRESSION PIN. CbvSrvUavHeapDirectlyIndexed (D3D12_ROOT_SIGNATURE_FLAG_
        // CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED) exists only to enable SM 6.6
        // ResourceDescriptorHeap[]/SamplerDescriptorHeap[]. The in-box Windows 10 D3D12
        // runtime caps at SM 6.5 and this app ships no Agility SDK, so setting it made
        // CreateRootSignature fail with E_INVALIDARG on EVERY Windows 10 machine — a blank
        // 3D view at any feature level, on any GPU. Our unbounded arrays are ordinary
        // descriptor ranges needing only Resource Binding Tier 2 (feature level 12_0).
        var rootSignature = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuRootSignature12.cs");

        var flags = SourceContract.Extract(
            rootSignature, "var desc = new RootSignatureDescription1(", "staticSamplers);");
        Assert.Contains("RootSignatureFlags.AllowInputAssemblerInputLayout", flags, StringComparison.Ordinal);
        Assert.DoesNotContain("HeapDirectlyIndexed", flags, StringComparison.Ordinal);

        // The flag would only ever be justified if a shader actually used the SM 6.6 syntax.
        foreach (var shader in Directory.EnumerateFiles(
                     SourceContract.ShadersRoot, "*.hlsl*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(shader);
            Assert.DoesNotContain("ResourceDescriptorHeap", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SamplerDescriptorHeap", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void SharedRootSignatureBuildsOnARealDevice()
    {
        GpuTestGuard.SkipUnlessEnabled();

        // End-to-end proof of the Windows 10 blocker's fix: the shared root signature is the
        // stage that failed AFTER device creation. Building it on WARP exercises the same
        // serializer path a no-GPU Windows 10 user hits.
        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);
        using var rootSignature = GpuRootSignature12.Create(device);
        Assert.NotNull(rootSignature);
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

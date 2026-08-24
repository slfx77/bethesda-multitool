using System.Text.RegularExpressions;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Pins a cross-file constant coupling that nothing else can enforce: the bounded HLSL depth
///     array in <c>water_common.hlsli</c> must be at least the descriptor heap's persistent
///     capacity.
///     <para>
///         This is the sanctioned use of a source pin — the two values live in different languages,
///         the shader's array bound is a compile-time literal, and the root-signature descriptor
///         range is UNBOUNDED (<c>NumDescriptors = uint.MaxValue</c>), so nothing at build or run
///         time relates them. Indexing past the HLSL bound is an out-of-bounds descriptor read
///         (wrong texture sampled, or device removal) — silent, not a compile error.
///     </para>
///     <para>
///         It really drifted: the persistent region was widened 16384 → 49152 while the shader kept
///         <c>[16384]</c>, leaving the invariant held only by the incidental fact that depth SRVs are
///         allocated early in the bump region.
///     </para>
/// </summary>
public sealed class WaterDepthArrayBoundTests
{
    [Fact]
    public void HlslDepthArrayBound_IsAtLeastTheDescriptorHeapPersistentCapacity()
    {
        var hlsl = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders",
            "Include", "water_common.hlsli");
        var device = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D", "WorldView3DControl.Device.cs");

        var hlslMatch = Regex.Match(hlsl, @"gWaterDepthTexturesMsaa\s*\[\s*(\d+)\s*\]");
        Assert.True(hlslMatch.Success, "gWaterDepthTexturesMsaa must keep an explicit array bound — " +
                                       "the unbounded [] form misresolves indices on the deployed driver.");

        var persistentMatch = Regex.Match(device, @"persistentCapacity\s*:\s*(\d+)");
        Assert.True(persistentMatch.Success, "persistentCapacity not found in WorldView3DControl.Device.cs");

        var hlslBound = int.Parse(hlslMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var persistentCapacity = int.Parse(
            persistentMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(hlslBound >= persistentCapacity,
            $"water_common.hlsli bounds gWaterDepthTexturesMsaa at {hlslBound}, but the descriptor heap's " +
            $"persistent region is {persistentCapacity}. Depth SRVs are persistent slots and the " +
            "root-signature range is unbounded, so a persistent index >= the HLSL bound is an " +
            "out-of-bounds descriptor read. Raise the HLSL bound to match.");
    }
}

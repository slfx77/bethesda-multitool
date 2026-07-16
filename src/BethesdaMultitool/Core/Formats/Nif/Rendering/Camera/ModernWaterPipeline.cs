using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Games;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Recovered Creation-era water technique bits. Only the two empirically proven feature bits
///     are named: <c>+0x40</c> selects the depth LUT and <c>+0x100</c> samples a cubemap. The other
///     shipped technique bits remain intentionally uninterpreted until SetupTechnique is recovered.
/// </summary>
[Flags]
internal enum ModernWaterTechnique : uint
{
    Baseline = 0x001,
    DepthLut = 0x040,
    Cubemap = 0x100,
}

/// <summary>
///     Pure selection, layout, and resource-state contract for the opt-in FO4/FO76 dynamic-water
///     path. Keeping this outside the D3D12-only renderer lets cross-platform tests pin the contract
///     without constructing a device or using production shader code as their reference oracle.
/// </summary>
internal static class ModernWaterPipeline
{
    internal const uint SurfaceDimension = 256;
    internal const uint DepthLutWidth = 256;
    internal const uint DepthLutHeight = 1;
    internal const uint MaxPointLights = 20;
    internal const uint PrepassUniformByteSize = 8 * 16;
    internal const uint FrameUniformByteSize = 26 * 16;

    // Authored normal inputs are consumed by compute and graphics paths. Dynamic outputs are read
    // by the pixel shader between per-frame UAV writes.
    internal const ResourceStates AuthoredSourceReadState =
        ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;
    internal const ResourceStates DynamicReadState = ResourceStates.PixelShaderResource;
    internal const ResourceStates DynamicWriteState = ResourceStates.UnorderedAccess;

    internal static bool Supports(BethesdaGame game) =>
        game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76;

    internal static bool ShouldUse(bool explicitlyEnabled, BethesdaGame game) =>
        explicitlyEnabled && Supports(game);

    internal static ModernWaterTechnique SelectTechnique(bool hasDepthLut, bool hasCubemap)
    {
        var technique = ModernWaterTechnique.Baseline;
        if (hasDepthLut) technique |= ModernWaterTechnique.DepthLut;
        if (hasCubemap) technique |= ModernWaterTechnique.Cubemap;
        return technique;
    }

    internal static string TelemetryName(
        BethesdaGame game,
        bool explicitlyEnabled,
        bool resourcesReady,
        bool prepassesRecorded,
        ModernWaterTechnique technique = ModernWaterTechnique.Baseline)
    {
        var prefix = game == BethesdaGame.Fallout76 ? "fo76" : game == BethesdaGame.Fallout4 ? "fo4" : "legacy";
        if (!ShouldUse(explicitlyEnabled, game)) return $"{prefix}-standin";
        if (!resourcesReady) return $"{prefix}-modern-unavailable-standin";
        if (!prepassesRecorded) return $"{prefix}-modern-prepass-fallback";
        return $"{prefix}-modern-0x{(uint)technique:x3}";
    }

    /// <summary>
    ///     With a neutral gloss-map sample of one, these scales preserve the authored WATR
    ///     SunPower and SunSpecularMagnitude through the recovered shader equations:
    ///     <c>exp(10*y*A+1)</c> and <c>x*B*pi</c>. They are mappings, not tuned constants.
    /// </summary>
    internal static float GlossPowerScale(float authoredSunPower) =>
        (MathF.Log(MathF.Max(authoredSunPower, 1e-6f)) - 1f) / 10f;

    internal static float GlossAmplitudeScale(float authoredMagnitude) =>
        authoredMagnitude / MathF.PI;
}

/// <summary>
///     CPU mirror of <c>ModernWaterPrepass</c> in water_modern.comp.hlsl. Eight 16-byte registers,
///     with raw uint texture indices in the first register so bindless indices remain bit-exact.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ModernWaterPrepassUniforms
{
    internal uint NormalIndex1;
    internal uint NormalIndex2;
    internal uint NormalIndex3;
    internal uint ReservedSource;
    internal Vector4 ShallowCoverage;
    internal Vector4 DeepCoverage;
    internal Vector4 Layer1;
    internal Vector4 Layer2;
    internal Vector4 Layer3;
    internal Vector4 NormalParams;
    internal Vector4 Ranges;
}

/// <summary>Four-register tail appended to the shared water b0 constant buffer.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ModernWaterFrameUniforms
{
    internal uint BodyIndex;
    internal uint NormalIndex;
    internal uint GlossIndex;
    internal uint DepthLutIndex;
    internal uint TechniqueId;
    internal uint CubeIndex;
    internal uint MaxPointLights;
    internal uint Reserved;
    internal Vector4 Params;
    internal Vector4 LightSilt;
}

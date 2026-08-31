using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Evidence-bounded bridge from Starfield's exact WATR payload to the viewer's first dedicated
///     Starfield water shader. This is deliberately named an approximation: the three texture-slot
///     assignments come from shipped global water assets and <c>WaterPlaceholder.mat</c> engine
///     substitution, while the CE2 Water DXIL bindings, CUR3 evaluation, and materialsbeta.cdb
///     constants remain unrecovered.
/// </summary>
internal sealed record StarfieldWaterApproximation
{
    internal const string TelemetryName = "starfield-watr-source-backed-approx";

    internal static readonly IReadOnlyList<string> InferredGlobalTexturePaths = Array.AsReadOnly(
    new[]
    {
        @"textures\water\defaultwater_normal.dds",
        @"textures\water\defaultwatertile_normal.dds",
        @"textures\water\defaultflow_normal.dds"
    });

    internal static readonly IReadOnlyList<string> InferredGlobalTextureRoles = Array.AsReadOnly(
    new[]
    {
        "starfield-global-normal-primary-inferred-slot",
        "starfield-global-normal-tile-inferred-slot",
        "starfield-global-flow-inferred-slot"
    });

    private StarfieldWaterApproximation(StarfieldWaterVisualData visualData)
    {
        VisualData = visualData;
    }

    internal StarfieldWaterVisualData VisualData { get; }

    /// <summary>
    ///     Retains the exact typed payload without consulting WATR ANAM. xEdit labels Starfield's
    ///     ANAM field "Opacity (unused)", so using it as surface coverage would manufacture a CE2
    ///     binding that the source explicitly says does not exist.
    /// </summary>
    internal static StarfieldWaterApproximation? FromWaterRecord(WaterRecord? water)
    {
        if (water?.VisualProperties is null ||
            !water.VisualProperties.TryGetValue("StarfieldVisualData", out var value) ||
            value is not StarfieldWaterVisualData visualData)
        {
            return null;
        }

        return new StarfieldWaterApproximation(visualData);
    }

    internal StarfieldWaterFrameUniforms ProjectFrameUniforms()
    {
        var dnam = VisualData.Dnam;
        var linear = VisualData.LinearVelocity;
        var angular = VisualData.AngularVelocity;
        return new StarfieldWaterFrameUniforms
        {
            Surface = new Vector4(
                dnam.Roughness,
                dnam.NormalMagnitude,
                dnam.ShallowNormalFalloff,
                dnam.DeepNormalFalloff),
            DepthFlow = new Vector4(
                dnam.DepthAmount,
                dnam.FlowmapScale,
                dnam.Oceanness,
                dnam.SurfaceEffectFalloff),
            Layer1 = PackLayer(dnam.Layer1),
            Layer2 = PackLayer(dnam.Layer2),
            Layer3 = PackLayer(dnam.Layer3),
            LayerFalloffsFlags = new Vector4(
                dnam.Layer1.NoiseFalloff,
                dnam.Layer2.NoiseFalloff,
                dnam.Layer3.NoiseFalloff,
                BitConverter.Int32BitsToSingle((byte)VisualData.Flags)),
            LinearVelocity = new Vector4(
                linear?.X ?? 0f,
                linear?.Y ?? 0f,
                linear?.Z ?? 0f,
                linear.HasValue ? 1f : 0f),
            AngularVelocity = new Vector4(
                angular?.X ?? 0f,
                angular?.Y ?? 0f,
                angular?.Z ?? 0f,
                angular.HasValue ? 1f : 0f),
            Displacement0 = new Vector4(
                dnam.DisplacementForce,
                dnam.DisplacementVelocity,
                dnam.DisplacementFalloff,
                dnam.DisplacementDampener),
            Displacement1 = new Vector4(
                dnam.DisplacementStartingSize,
                dnam.UnderwaterFogAmount,
                dnam.UnderwaterFogNear,
                dnam.UnderwaterFogFar),
            Absorption = new Vector4(
                dnam.AbsorptionRanges.R,
                dnam.AbsorptionRanges.G,
                dnam.AbsorptionRanges.B,
                0f),
            Concentrations = new Vector4(
                dnam.PhytoplanktonConcentration,
                dnam.SedimentConcentration,
                dnam.YellowMatterConcentration,
                dnam.Oceanness),
            UnderwaterColor = new Vector4(
                dnam.UnderwaterColor.R / 255f,
                dnam.UnderwaterColor.G / 255f,
                dnam.UnderwaterColor.B / 255f,
                dnam.UnderwaterColor.A / 255f)
        };
    }

    private static Vector4 PackLayer(StarfieldWaterNoiseLayer layer) => new(
        layer.UvScale,
        layer.WindDirection,
        layer.WindSpeed,
        layer.AmplitudeScale);
}

/// <summary>
///     Append-only CPU/HLSL tail for the dedicated Starfield approximation. Every decoded scalar
///     used by this bounded visual slice reaches the GPU without being folded into a classic
///     shallow/deep-water model. CUR3 FormIDs remain on <see cref="StarfieldWaterVisualData" /> until
///     their evaluation contract is recovered; opaque GNAM words likewise remain ingestion data
///     rather than shader constants.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StarfieldWaterFrameUniforms
{
    internal const int RegisterCount = 13;

    public Vector4 Surface;
    public Vector4 DepthFlow;
    public Vector4 Layer1;
    public Vector4 Layer2;
    public Vector4 Layer3;
    public Vector4 LayerFalloffsFlags;
    public Vector4 LinearVelocity;
    public Vector4 AngularVelocity;
    public Vector4 Displacement0;
    public Vector4 Displacement1;
    public Vector4 Absorption;
    public Vector4 Concentrations;
    public Vector4 UnderwaterColor;
}

using System.Numerics;
using System.Runtime.InteropServices;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>Recorded source UV transform. No inferred viewport or D3D9 half-texel correction.</summary>
internal readonly record struct OblivionWaterRecordedRaster(Vector4 TexRatio, float HmapSampleStep);

/// <summary>Six float4 root-constant registers in water_oblivion_displace.hlsl.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct OblivionWaterDisplacementConstants(
    Vector4 TexRatio0,
    Vector4 Translation0,
    Vector4 Translation1,
    Vector4 Simulation,
    Vector4 Surface,
    Vector4 Sampling)
{
    internal const uint DwordCount = 24;
}

internal static class OblivionWaterDisplacementShaderAbi
{
    internal const string FileName = "water_oblivion_displace.hlsl";

    internal static (string Vertex, string Pixel) Entries(OblivionWaterSimulationStage stage)
    {
        return stage switch
        {
            OblivionWaterSimulationStage.WadingStamp => ("vsWadingStamp", "psWadingStamp"),
            OblivionWaterSimulationStage.RainStamp => ("vsRainStamp", "psRainStamp"),
            OblivionWaterSimulationStage.WadingEvolution => ("vsQuad", "psWadingEvolution"),
            OblivionWaterSimulationStage.RainEvolution => ("vsQuad", "psRainEvolution"),
            OblivionWaterSimulationStage.Normal => ("vsQuad", "psNormal"),
            OblivionWaterSimulationStage.MixedHeight => ("vsQuad", "psMixedHeight"),
            OblivionWaterSimulationStage.Recenter => ("vsQuad", "psRecenter"),
            OblivionWaterSimulationStage.FftNormal => ("vsQuad", "psFftNormal"),
            OblivionWaterSimulationStage.FftAbsoluteHeight => ("vsQuad", "psFftAbsoluteHeight"),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
    }

    internal static OblivionWaterDisplacementConstants Constants(
        OblivionWaterSimulationPass pass, OblivionWaterRecordedRaster raster)
    {
        ArgumentNullException.ThrowIfNull(pass);
        _ = Entries(pass.Stage);
        if (!Finite(raster.TexRatio) || !float.IsFinite(raster.HmapSampleStep))
            throw new ArgumentException("Recorded raster constants must be finite.", nameof(raster));
        if (pass.Stage == OblivionWaterSimulationStage.FftNormal && raster.HmapSampleStep <= 0f)
            throw new ArgumentException("HMAP005 needs its recorded positive sample step.", nameof(raster));
        if (pass.Stage == OblivionWaterSimulationStage.MixedHeight && pass.Dampener.Equals(0f))
            throw new ArgumentException("The authored zero dampener makes phase6 reciprocal undefined.", nameof(pass));

        var ratio = raster.TexRatio;
        var row0 = Vector4.Zero;
        var row1 = Vector4.Zero;
        if (pass.Stage == OblivionWaterSimulationStage.RainStamp)
            ratio = new Vector4(pass.StampScale, pass.StampScale, pass.Offset.X, pass.Offset.Y);
        if (pass.Stage == OblivionWaterSimulationStage.WadingStamp)
        {
            var stamp = pass.WadingStamp ??
                        throw new ArgumentException("Phase0 needs its recorded matrix.", nameof(pass));
            row0 = stamp.Row0;
            row1 = stamp.Row1;
        }

        return new OblivionWaterDisplacementConstants(ratio, row0, row1, new Vector4(pass.Controls, pass.Dampener),
            new Vector4(pass.BlendAmount, pass.Offset.X, pass.Offset.Y, raster.HmapSampleStep),
            new Vector4(pass.Address == OblivionWaterSimulationAddress.Clamp ? 1f : 0f, 0f, 0f, 0f));
    }

    private static bool Finite(Vector4 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}

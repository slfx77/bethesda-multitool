using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     The two Skyrim-only union registers carried in the existing Fo4Spec/Fo4Ranges slots.
///     TESV material +A0 is logical pixel constant 21 (DepthControl); +F8/+FC/+100 provide the
///     above-water far/span/power. Zero is an explicitly disabled payload, never a guessed material.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct SkyrimWaterOpticsConstants(Vector4 DepthControl, Vector4 Fog)
{
    internal const int ByteSize = 32;
    internal bool IsEnabled => Fog.W.Equals(1f); // Exact uploaded discriminator, not an approximate measurement.

    internal static SkyrimWaterOpticsConstants Project(
        BethesdaGame game, bool hasSkyrimOpaqueSnapshot, SkyrimWaterOptics? source)
    {
        if (game != BethesdaGame.Skyrim || !hasSkyrimOpaqueSnapshot || source is not { IsFinite: true })
            return default;

        var span = source.AboveWaterFogFar - source.AboveWaterFogNear;
        if (span.Equals(0f)) span += 1e-5f; // TESV0054DFC3..0054DFDA: exact zero alone receives epsilon.
        return new SkyrimWaterOpticsConstants(
            new Vector4(source.DepthControl.Reflections, source.DepthControl.Refraction,
                source.DepthControl.Normals, source.DepthControl.Specular),
            new Vector4(MathF.Max(1f, source.AboveWaterFogFar), span,
                MathF.Max(1f, 4f * source.AboveWaterFogAmount), 1f));
    }
}

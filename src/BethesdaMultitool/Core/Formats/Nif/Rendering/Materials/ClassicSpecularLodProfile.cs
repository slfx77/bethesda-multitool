using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Classic FNV specular-LOD settings. The shipped defaults are
///     <c>fSpecularLODDefaultStartFade=500</c>, <c>fSpecularLODRange=300</c>, and
///     <c>fLODAdjust=1</c>.
/// </summary>
internal readonly record struct ClassicSpecularLodProfile(
    bool Supported,
    float StartFade,
    float FadeRange,
    float LodAdjust)
{
    internal float EndFade => StartFade + FadeRange;

    internal bool Enabled =>
        Supported &&
        float.IsFinite(StartFade) &&
        float.IsFinite(EndFade) &&
        float.IsFinite(LodAdjust) &&
        EndFade > 0f;

    internal static ClassicSpecularLodProfile ForGame(BethesdaGame game)
    {
        return game switch
        {
            // FO3 parity 2026-08-10: FO3's shipped Fallout_default.ini authors the identical
            // fSpecularLODDefaultStartFade=500 / fSpecularLODRange=300 pair, so the same envelope
            // applies. The fade LAW (sphere-surface distance, piecewise ramp) remains an FNV
            // decompile recovery assumed shared — flagged for opportunistic FO3 confirmation.
            BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 =>
                new ClassicSpecularLodProfile(true, 500f, 300f, 1f),
            _ => default
        };
    }

    /// <summary>
    ///     x=start, y=end, z=LOD adjust, w=enabled. A stinger draw bypasses the retail fade.
    /// </summary>
    internal Vector4 ShaderParameters(bool specularEligible, bool isStinger = false)
    {
        return new Vector4(StartFade, EndFade, LodAdjust, Enabled && specularEligible && !isStinger ? 1f : 0f);
    }
}

/// <summary>CPU mirror of the retail sphere-surface specular-LOD distance and piecewise fade.</summary>
internal static class ClassicSpecularLodFade
{
    internal static float Compute(
        in ClassicSpecularLodProfile profile,
        Vector3 localCenter,
        float localRadius,
        Matrix4x4 world,
        Vector3 cameraPosition,
        bool specularEligible = true,
        bool isStinger = false)
    {
        if (!profile.Enabled || !specularEligible || isStinger ||
            !IsFinite(localCenter) || !float.IsFinite(localRadius) || localRadius < 0f ||
            !IsFinite(cameraPosition))
        {
            return 1f;
        }

        var worldCenter = Vector3.Transform(localCenter, world);
        var scaleX = new Vector3(world.M11, world.M12, world.M13).Length();
        var scaleY = new Vector3(world.M21, world.M22, world.M23).Length();
        var scaleZ = new Vector3(world.M31, world.M32, world.M33).Length();
        var worldRadius = localRadius * MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
        if (!IsFinite(worldCenter) || !float.IsFinite(worldRadius))
        {
            return 1f;
        }

        var centerDistance = Vector3.Distance(worldCenter, cameraPosition);
        return ComputeFromCameraDistance(in profile, centerDistance, worldRadius, specularEligible, isStinger);
    }

    internal static float ComputeFromCameraDistance(
        in ClassicSpecularLodProfile profile,
        float centerDistance,
        float worldRadius,
        bool specularEligible = true,
        bool isStinger = false)
    {
        if (!profile.Enabled || !specularEligible || isStinger ||
            !float.IsFinite(centerDistance) || !float.IsFinite(worldRadius) || worldRadius < 0f)
        {
            return 1f;
        }

        // Retail measures from the authored sphere surface, then applies the global LOD scalar.
        // Do not clamp: a camera inside the sphere intentionally produces a negative distance.
        var distance = (centerDistance - worldRadius) * profile.LodAdjust;
        if (distance < profile.StartFade)
        {
            return 1f;
        }

        if (distance >= profile.EndFade)
        {
            return 0f;
        }

        return 1f - (distance - profile.StartFade) / (profile.EndFade - profile.StartFade);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}

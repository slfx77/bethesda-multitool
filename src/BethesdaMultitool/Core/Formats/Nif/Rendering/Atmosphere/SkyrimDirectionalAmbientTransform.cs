using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Skyrim retail's affine projection of the six authored CELL/WTHR directional-ambient
///     colours. These are the three rows uploaded as the lighting shader's row-major
///     <c>float3x4 DirectionalAmbient</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct SkyrimDirectionalAmbientTransform
{
    // SkyrimSE 1.7.104, RVA 0x14EC3B0 (Address Library AE ID 105643): the writer uses the exact
    // IEEE-754 literals 0x3F000000 and 0x3E2AAAAB for these two operations.
    private const float DifferenceScale = 0.5f;
    private const float MeanScale = 1f / 6f;

    internal SkyrimDirectionalAmbientTransform(Vector4 row0, Vector4 row1, Vector4 row2)
    {
        Row0 = row0;
        Row1 = row1;
        Row2 = row2;
    }

    internal readonly Vector4 Row0;
    internal readonly Vector4 Row1;
    internal readonly Vector4 Row2;

    /// <summary>
    ///     Selects the retail affine path only for Skyrim. Fallout 76 retains the existing
    ///     six-face/squared-normal projection until its own retail shader establishes otherwise.
    /// </summary>
    internal static bool TryCreate(
        BethesdaGame game,
        AtmosphereState.ResolvedAmbientCube? directionalAmbient,
        out SkyrimDirectionalAmbientTransform transform)
    {
        if (game == BethesdaGame.Skyrim && directionalAmbient is { } cube)
        {
            transform = Create(cube);
            return true;
        }

        transform = default;
        return false;
    }

    /// <summary>
    ///     Packs the retail matrix exactly: each axis column is one half of
    ///     <c>negativeFace - positiveFace</c>, and the translation is the mean of all six faces.
    ///     Consequently a +axis surface normal evaluates toward the authored -axis colour; this
    ///     sign is deliberate and comes from the retail writer rather than a face-label guess.
    /// </summary>
    internal static SkyrimDirectionalAmbientTransform Create(AtmosphereState.ResolvedAmbientCube cube)
    {
        var translation = cube.PositiveX + cube.NegativeX;
        translation += cube.PositiveY + cube.NegativeY;
        translation += cube.PositiveZ + cube.NegativeZ;
        translation *= MeanScale;

        var x = (cube.NegativeX - cube.PositiveX) * DifferenceScale;
        var y = (cube.NegativeY - cube.PositiveY) * DifferenceScale;
        var z = (cube.NegativeZ - cube.PositiveZ) * DifferenceScale;

        return new SkyrimDirectionalAmbientTransform(
            new Vector4(x.X, y.X, z.X, translation.X),
            new Vector4(x.Y, y.Y, z.Y, translation.Y),
            new Vector4(x.Z, y.Z, z.Z, translation.Z));
    }

    /// <summary>CPU oracle for the shader's three row dot-products; input must be unit length.</summary>
    internal Vector3 EvaluateUnitNormal(Vector3 unitNormal)
    {
        var homogeneousNormal = new Vector4(unitNormal, 1f);
        return new Vector3(
            Vector4.Dot(Row0, homogeneousNormal),
            Vector4.Dot(Row1, homogeneousNormal),
            Vector4.Dot(Row2, homogeneousNormal));
    }
}

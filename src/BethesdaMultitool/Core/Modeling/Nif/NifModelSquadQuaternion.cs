namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A quaternion in the engine's own component order, W then X, Y, Z (NiQuaternion; the file order of a QuatKey), for
///     the Float32 arithmetic of <see cref="NifModelSquadInnerPoints" />. Shared's order (X, Y, Z, W) is a permutation of
///     this one with no conjugation.
/// </summary>
/// <param name="W">The scalar part.</param>
/// <param name="X">The x component.</param>
/// <param name="Y">The y component.</param>
/// <param name="Z">The z component.</param>
internal readonly record struct NifModelSquadQuaternion(float W, float X, float Y, float Z)
{
    /// <summary>True when every component is finite.</summary>
    public bool IsFinite => float.IsFinite(W) && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);

    /// <summary>True when every component is zero (either sign), the value Shared refuses as a key or inner point.</summary>
    public bool IsZero => W == 0f && X == 0f && Y == 0f && Z == 0f;

    /// <summary>The conjugate (W, -X, -Y, -Z), the unit inverse the engine uses (NiQuaternion::UnitInverse).</summary>
    /// <returns>The conjugate.</returns>
    public NifModelSquadQuaternion Conjugate()
    {
        return new NifModelSquadQuaternion(W, -X, -Y, -Z);
    }

    /// <summary>Writes the quaternion in Shared's order X, Y, Z, W at an offset of a value array.</summary>
    /// <param name="values">The destination.</param>
    /// <param name="offset">The index of the X component.</param>
    public void WriteXyzw(float[] values, int offset)
    {
        ArgumentNullException.ThrowIfNull(values);
        values[offset] = X;
        values[offset + 1] = Y;
        values[offset + 2] = Z;
        values[offset + 3] = W;
    }
}

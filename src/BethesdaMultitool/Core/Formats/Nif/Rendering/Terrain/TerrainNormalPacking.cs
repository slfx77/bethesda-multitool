using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Octahedral packing of a unit normal into two SNORM16 components — the encoding half of
///     <see cref="TerrainVertex" />'s 4-byte normal, against 12 bytes for three floats.
///     <para>
///         <b>Why this is not a fidelity loss.</b> The dominant source of terrain normals is LAND's
///         <c>VNML</c> subrecord, which stores three <i>signed bytes</i> per vertex; its angular
///         lattice is roughly 0.45° between representable directions. This encoding's worst case,
///         measured over a 200k-sample sweep of the whole sphere, is <b>0.00365°</b> — about 120×
///         finer than the data being encoded, and reached only in the hemisphere-fold region where
///         the octahedral mapping stretches most. For the computed-normal fallback (central
///         differences over float heights) the bound still sits far below the ~0.2° of normal change
///         an 8-bit output channel can register under Lambert shading, so nothing survives to the
///         framebuffer.
///     </para>
///     <para>
///         Full-sphere octahedral rather than hemi-octahedral: terrain normals are overwhelmingly
///         +Z, but <c>VNML</c> is authored data and a malformed or runtime-derived record can present
///         a downward normal. A hemispherical mapping would fold those onto the wrong direction
///         silently, and the extra bit of precision it buys is already far past the point of
///         mattering.
///     </para>
///     <para>
///         <see cref="Decode" /> mirrors the GPU exactly, including D3D12's SNORM rule that
///         <c>-32768</c> and <c>-32767</c> both decode to <c>-1</c>, so a CPU-side check of what the
///         shader will see is trustworthy. <see cref="Encode" /> never emits <c>-32768</c>.
///     </para>
/// </summary>
internal static class TerrainNormalPacking
{
    /// <summary>SNORM16 scale. 32767, not 32768 — D3D12 divides by (2^15 - 1) and clamps at -1.</summary>
    public const float SnormScale = 32767f;

    /// <summary>
    ///     Packs <paramref name="normal" /> (normalised here if it is not already) into the
    ///     octahedral SNORM16 pair. A degenerate (zero-length) normal becomes +Z, matching the
    ///     fallback <c>TerrainMeshBuilder</c> already applies to a zero <c>VNML</c> entry.
    /// </summary>
    public static void Encode(Vector3 normal, out short x, out short y)
    {
        var lengthSquared = normal.LengthSquared();
        var n = lengthSquared > 1e-12f ? normal / MathF.Sqrt(lengthSquared) : Vector3.UnitZ;

        // Project onto the octahedron: the L1 norm of a unit vector is in [1, sqrt(3)], so this
        // cannot divide by zero once the normalise above has run.
        var l1 = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        var px = n.X / l1;
        var py = n.Y / l1;

        if (n.Z <= 0f)
        {
            // Unfold the lower hemisphere onto the outer square.
            var foldedX = (1f - MathF.Abs(py)) * SignNotZero(px);
            var foldedY = (1f - MathF.Abs(px)) * SignNotZero(py);
            px = foldedX;
            py = foldedY;
        }

        x = Quantise(px);
        y = Quantise(py);
    }

    /// <summary>
    ///     Unpacks an octahedral SNORM16 pair back to a unit normal, reproducing the shader's decode
    ///     step for step so a CPU assertion means what the GPU will do.
    /// </summary>
    public static Vector3 Decode(short x, short y)
    {
        // D3D12 SNORM: value / 32767, floored at -1 so the two most-negative codes coincide.
        var ex = MathF.Max(x / SnormScale, -1f);
        var ey = MathF.Max(y / SnormScale, -1f);
        var ez = 1f - MathF.Abs(ex) - MathF.Abs(ey);

        if (ez < 0f)
        {
            var foldedX = (1f - MathF.Abs(ey)) * SignNotZero(ex);
            var foldedY = (1f - MathF.Abs(ex)) * SignNotZero(ey);
            ex = foldedX;
            ey = foldedY;
        }

        var decoded = new Vector3(ex, ey, ez);
        var length = decoded.Length();
        return length > 0f ? decoded / length : Vector3.UnitZ;
    }

    /// <summary>Round-trip error in degrees for <paramref name="normal" />. Diagnostics and tests.</summary>
    public static double RoundTripErrorDegrees(Vector3 normal)
    {
        Encode(normal, out var x, out var y);
        // Same degenerate-input guard Encode applies, so a zero normal reports 0° rather than NaN.
        var lengthSquared = normal.LengthSquared();
        var reference = lengthSquared > 1e-12f ? normal / MathF.Sqrt(lengthSquared) : Vector3.UnitZ;
        return AngleDegrees(reference, Decode(x, y));
    }

    /// <summary>
    ///     Angle between two unit vectors, via the chord rather than <c>acos(dot)</c>.
    ///     <para>
    ///         ⚠ This distinction is the whole reason the helper exists. For nearly-parallel vectors
    ///         the dot product is 1 − θ²/2, so in single precision every angle below ~0.03° collapses
    ///         into the same handful of representable values and <c>acos</c> reports the square root
    ///         of the float epsilon instead of the actual angle — a noise floor an order of magnitude
    ///         larger than the error this packing is supposed to be measured against. The chord
    ///         <c>|a − b|</c> is computed accurately for close vectors, so <c>2·asin(|a − b|/2)</c>
    ///         stays meaningful all the way down.
    ///     </para>
    /// </summary>
    public static double AngleDegrees(Vector3 a, Vector3 b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        var chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return 2.0 * Math.Asin(Math.Clamp(chord / 2.0, 0.0, 1.0)) * (180.0 / Math.PI);
    }

    /// <summary>+1 for zero, matching the shader's <c>x &gt;= 0 ? +1 : -1</c> so both folds agree.</summary>
    private static float SignNotZero(float value) => value >= 0f ? 1f : -1f;

    private static short Quantise(float value) =>
        (short)MathF.Round(Math.Clamp(value, -1f, 1f) * SnormScale);
}

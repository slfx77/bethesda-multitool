using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     RE-17 (docs/formats/nif-animation-engine-behavior-20260925.md): the engine's orientation at each key of a LINEAR or
///     CONST quaternion key group, derived from the raw keys exactly as FNV/FO3 do it, and provisionally of a TBC group
///     (see the remarks). A pure function over the lossless view (<see cref="NifKeyGroupView" />); the renderer never
///     calls it.
/// </summary>
/// <remarks>
///     <para>The three steps, each in binary32 with the documented operation order:</para>
///     <list type="number">
///         <item>
///             Chain sign alignment at load (NiRotKey::FillDerivedVals, FNV 0xA28AB0), in file order and in place:
///             d = ((x1*x0 + w0*w1) + y1*y0) + z1*z0, and key i+1 is negated (all four components) when d &lt; 0. Key i is
///             the already-negated key, so this is a chain. A dot of +0, -0 or NaN never flips.
///         </item>
///         <item>W clamped to [-1, 1] on every key, after all flips (the engine's second pass; it never fires on retail).</item>
///         <item>
///             The exact normalize of NiBlendTransformInterpolator::StoreSingleValue (FNV 0xA38560):
///             s = ((x*x + w*w) + y*y) + z*z, len = sqrt(s), inv = 1/len, each component times inv. Multiplying by the
///             reciprocal is not dividing by the length: the two differ in the last bit on retail keys.
///         </item>
///     </list>
///     <para>
///         Every operation is a float operation on float locals: no double literal or intermediate, no fused multiply-add,
///         and <see cref="MathF.Sqrt" />, which is correctly rounded. The engine's Slerp between keys (Blow's counter-warped
///         nlerp) is not reproduced; this rule gives the orientation AT each key only. B-spline control points and
///         XYZ-Euler axes are out of scope (RE-17 step 5 and RE-20); a QUADRATIC quaternion group has its own fill
///         function in the engine and is refused.
///     </para>
///     <para>
///         TBC: RE-17 settles every step for LINEAR and CONST (their fill function, 0xA2D670, jumps to
///         NiRotKey::FillDerivedVals 0xA28AB0). A TBC group has its own fill function (NiTCBRotKey, 0xA2C130 in the fill
///         table at 0x11F37B0), which RE-24 read: it runs the same chain flip and W clamp (steps 1 and 2, exposed here as
///         <see cref="AlignChainAndClampW" />) and then derives Squad inner points from the UNNORMALIZED keys, so step 3
///         must not run before that path (BethesdaMultitool.Core.Modeling.Nif.NifModelSquadInnerPoints). The full three
///         steps still apply to a TBC group here for the load-time state; the value at a TBC key time is the Squad at
///         u = 1 of the preceding segment, which equals the normalized key within 1.31e-5 degrees (RE-24), not bit for bit.
///     </para>
/// </remarks>
internal static class NifRotationKeyEngineRule
{
    /// <summary>
    ///     True for the key types the rule applies to: LINEAR (1) and CONST (5), settled by RE-17, and TBC (3), whose flip
    ///     and clamp are confirmed but whose value at a key is provisional until SA6 (see the remarks).
    /// </summary>
    internal static bool AppliesTo(uint keyType)
    {
        return keyType == (uint)NifKeyInterpolation.Linear ||
               keyType == (uint)NifKeyInterpolation.Tbc ||
               keyType == (uint)NifKeyInterpolation.Constant;
    }

    /// <summary>
    ///     Applies the rule to a quaternion key group. False (and no result) for a group that is not quaternion-valued, is
    ///     empty, or has a key type the rule does not govern (QUADRATIC, or anything else).
    /// </summary>
    internal static bool TryApply(in NifKeyGroupView keys, [NotNullWhen(true)] out NifEngineRotationKeys? result)
    {
        result = null;
        if (keys.Layout != NifKeyValueLayout.Quaternion || keys.Count == 0 || !AppliesTo(keys.KeyType))
        {
            return false;
        }

        var count = keys.Count;
        var w = new float[count];
        var x = new float[count];
        var y = new float[count];
        var z = new float[count];
        for (var index = 0; index < count; index++)
        {
            // File order w, x, y, z.
            w[index] = keys.Value(index, 0);
            x[index] = keys.Value(index, 1);
            y[index] = keys.Value(index, 2);
            z[index] = keys.Value(index, 3);
        }

        result = Apply(w, x, y, z);
        return true;
    }

    /// <summary>
    ///     The rule over raw key components in file order (the four spans are parallel, one element per key). The inputs
    ///     are copied, never modified.
    /// </summary>
    internal static NifEngineRotationKeys Apply(
        ReadOnlySpan<float> w,
        ReadOnlySpan<float> x,
        ReadOnlySpan<float> y,
        ReadOnlySpan<float> z)
    {
        if (x.Length != w.Length || y.Length != w.Length || z.Length != w.Length)
        {
            throw new ArgumentException("The four component spans must have one element per key.");
        }

        var count = w.Length;
        var qw = w.ToArray();
        var qx = x.ToArray();
        var qy = y.ToArray();
        var qz = z.ToArray();

        // (1) and (2): chain sign alignment, then the W clamp.
        var negated = AlignChainAndClampW(qw, qx, qy, qz);

        // (3) The exact normalize, emitted as X, Y, Z, W.
        var values = new Quaternion[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = NormalizeExact(qw[index], qx[index], qy[index], qz[index]);
        }

        return new NifEngineRotationKeys(values, negated);
    }

    /// <summary>
    ///     RE-17 steps (1) and (2) alone, in place: the chain sign alignment of NiRotKey::FillDerivedVals in file order
    ///     (key i+1 negated when its dot with the already-aligned key i is below zero; +0, -0 and NaN never flip), then
    ///     the W clamp to [-1, 1] on every key. No normalization: this is the load-time state the TBC and QUADRATIC fills
    ///     derive their Squad inner points from (RE-24 step 1), and the state <see cref="Apply" /> normalizes for LINEAR
    ///     and CONST.
    /// </summary>
    /// <param name="w">The W components in file order, aligned and clamped in place.</param>
    /// <param name="x">The X components, aligned in place.</param>
    /// <param name="y">The Y components, aligned in place.</param>
    /// <param name="z">The Z components, aligned in place.</param>
    /// <returns>Per key, true when the chain alignment negated it (key 0 never is).</returns>
    /// <exception cref="ArgumentException">The four spans differ in length.</exception>
    internal static bool[] AlignChainAndClampW(Span<float> w, Span<float> x, Span<float> y, Span<float> z)
    {
        if (x.Length != w.Length || y.Length != w.Length || z.Length != w.Length)
        {
            throw new ArgumentException("The four component spans must have one element per key.");
        }

        var count = w.Length;
        var negated = new bool[count];

        // (1) Chain sign alignment, in file order, in place.
        for (var index = 0; index < count - 1; index++)
        {
            var next = index + 1;
            var dot = ChainDot(w[index], x[index], y[index], z[index], w[next], x[next], y[next], z[next]);
            if (dot < 0f)
            {
                w[next] = -w[next];
                x[next] = -x[next];
                y[next] = -y[next];
                z[next] = -z[next];
                negated[next] = true;
            }
        }

        // (2) W clamp, after every flip.
        for (var index = 0; index < count; index++)
        {
            if (w[index] < -1f)
            {
                w[index] = -1f;
            }
            else if (w[index] > 1f)
            {
                w[index] = 1f;
            }
        }

        return negated;
    }

    /// <summary>
    ///     The load-time dot of key i (w0, x0, y0, z0) and key i+1 (w1, x1, y1, z1) in the engine's order:
    ///     ((x1*x0 + w0*w1) + y1*y0) + z1*z0, every product and sum rounded to binary32.
    /// </summary>
    internal static float ChainDot(float w0, float x0, float y0, float z0, float w1, float x1, float y1, float z1)
    {
        var xx = x1 * x0;
        var ww = w0 * w1;
        var yy = y1 * y0;
        var zz = z1 * z0;
        var sum = xx + ww;
        sum += yy;
        sum += zz;
        return sum;
    }

    /// <summary>
    ///     The engine's exact normalize of one key (w, x, y, z in file order), returned as X, Y, Z, W:
    ///     s = ((x*x + w*w) + y*y) + z*z, len = sqrt(s), inv = 1/len, each component times inv.
    /// </summary>
    internal static Quaternion NormalizeExact(float w, float x, float y, float z)
    {
        var xx = x * x;
        var ww = w * w;
        var yy = y * y;
        var zz = z * z;
        var squared = xx + ww;
        squared += yy;
        squared += zz;
        var length = MathF.Sqrt(squared);
        var inverse = 1f / length;
        return new Quaternion(x * inverse, y * inverse, z * inverse, w * inverse);
    }
}

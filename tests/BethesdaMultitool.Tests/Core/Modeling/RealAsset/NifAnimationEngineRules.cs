using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The engine rules of <c>docs/formats/nif-animation-engine-behavior-20260925.md</c>, transcribed for the slice-9
///     oracles from the document's implementation-rule text alone (RE-17 steps 1 to 3 and its interpolation formula,
///     RE-19's Dv, RE-22 rule 3, RE-24 steps 2 and 3), never from the reader's code, so the hops compare the reader with
///     the written rule. Every operation is a Float32 operation on a float local in the rule's order (no double
///     intermediate, no fused multiply-add), except where a rule names a double: RE-24's acos, sin and cos
///     (<c>(float)Math.X((double)x)</c>) and RE-22's shifted phase (formed in double, rounded once).
/// </summary>
/// <remarks>Quaternions are float[4] in the file's W, X, Y, Z order unless a member says otherwise.</remarks>
internal static class NifAnimationEngineRules
{
    /// <summary>nif.xml's #INV_FLT# (-FLT_MAX), the static value of a channel an interpolator does not drive.</summary>
    public const uint InvalidFloatBits = 0xFF7FFFFF;

    /// <summary>+FLT_MAX, RE-22's start sentinel.</summary>
    public const uint StartSentinelBits = 0x7F7FFFFF;

    /// <summary>-FLT_MAX, RE-22's stop sentinel.</summary>
    public const uint StopSentinelBits = 0xFF7FFFFF;

    /// <summary>RE-24's PI_F.</summary>
    private static readonly float PiF = BitConverter.UInt32BitsToSingle(0x40490FDB);

    /// <summary>RE-24's EPS_F (0.001f).</summary>
    private static readonly float EpsilonF = BitConverter.UInt32BitsToSingle(0x3A83126F);

    /// <summary>RE-17's ATTEN (0.8227969f).</summary>
    private static readonly float Attenuation = BitConverter.UInt32BitsToSingle(0x3F52A2D1);

    /// <summary>RE-17's SLOPE (0.5854922f).</summary>
    private static readonly float Slope = BitConverter.UInt32BitsToSingle(0x3F15E2D1);

    /// <summary>
    ///     RE-17 steps 1 and 2, in place: the chain sign alignment in file order (a key is negated when the Float32 dot
    ///     ((x1*x0 + w0*w1) + y1*y0) + z1*z0 with the already aligned previous key is below zero), then the W clamp.
    /// </summary>
    /// <param name="keys">The keys, each W, X, Y, Z; changed in place.</param>
    /// <returns>Which keys the chain negated.</returns>
    public static bool[] AlignChainAndClampW(float[][] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var negated = new bool[keys.Length];
        for (var i = 0; i + 1 < keys.Length; i++)
        {
            var p = keys[i];
            var q = keys[i + 1];
            var d = ((q[1] * p[1] + p[0] * q[0]) + q[2] * p[2]) + q[3] * p[3];
            if (d < 0f)
            {
                q[0] = -q[0];
                q[1] = -q[1];
                q[2] = -q[2];
                q[3] = -q[3];
                negated[i + 1] = true;
            }
        }

        foreach (var key in keys)
        {
            if (key[0] < -1f)
            {
                key[0] = -1f;
            }
            else if (key[0] > 1f)
            {
                key[0] = 1f;
            }
        }

        return negated;
    }

    /// <summary>
    ///     RE-17 step 3, the exact normalize of NiBlendTransformInterpolator::StoreSingleValue:
    ///     s = ((x*x + w*w) + y*y) + z*z, L = sqrt(s), inv = 1/L, each component times inv.
    /// </summary>
    /// <param name="key">One key, W, X, Y, Z.</param>
    /// <returns>The normalized key in Shared's X, Y, Z, W order.</returns>
    public static float[] NormalizeExactXyzw(float[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var w = key[0];
        var x = key[1];
        var y = key[2];
        var z = key[3];
        var s = ((x * x + w * w) + y * y) + z * z;
        var length = MathF.Sqrt(s);
        var inverse = 1f / length;
        return [x * inverse, y * inverse, z * inverse, w * inverse];
    }

    /// <summary>
    ///     RE-17's LINEAR or CONST keys as Shared receives them: steps 1 to 3 over the raw keys, then W, X, Y, Z permuted to
    ///     X, Y, Z, W with no conjugation.
    /// </summary>
    /// <param name="raw">The raw keys, W, X, Y, Z, in file order (not changed).</param>
    /// <returns>One X, Y, Z, W quaternion per key.</returns>
    public static float[][] LinearRotationKeys(IReadOnlyList<float[]> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var keys = raw.Select(static key => (float[])key.Clone()).ToArray();
        AlignChainAndClampW(keys);
        return keys.Select(NormalizeExactXyzw).ToArray();
    }

    /// <summary>
    ///     RE-19's Dv (CalculateDVals for one component), in Float32 with the rule's operation order; returns the key's
    ///     incoming (DS) and outgoing (DD) tangents.
    /// </summary>
    public static (float In, float Out) Dv(float v, float t, float c, float b, float sub1, float plus1, float pre,
        float next)
    {
        var bwd = v - sub1;
        var fwd = plus1 - v;
        var opB = b + 1f;
        var omB = 1f - b;
        var h = (1f - t) * 0.5f;
        var opCh = (c + 1f) * h;
        var omCh = (1f - c) * h;
        var a1 = opCh * omB;
        var a2 = a1 * fwd;
        var a3 = omCh * opB;
        var a4 = a3 * bwd;
        var inRaw = a2 + a4;
        var d1 = omCh * omB;
        var d2 = d1 * fwd;
        var d3 = opCh * opB;
        var d4 = d3 * bwd;
        var outRaw = d2 + d4;
        var k = 2f / (pre + next);
        return (inRaw * (k * pre), outRaw * (k * next));
    }

    /// <summary>RE-19's StartOutgoing for one component: Dv at key 0 with the mirrored neighbor (P0 * 2) - P1.</summary>
    public static float StartOutgoing(float first, float second, float t, float c, float b)
    {
        var mirrored = (first * 2f) - second;
        return Dv(first, t, c, b, mirrored, second, 1f, 1f).Out;
    }

    /// <summary>RE-19's EndIncoming for one component: Dv at the last key with the mirrored neighbor (Pn-1 * 2) - Pn-2.</summary>
    public static float EndIncoming(float previous, float last, float t, float c, float b)
    {
        var mirrored = (last * 2f) - previous;
        return Dv(last, t, c, b, previous, mirrored, 1f, 1f).In;
    }

    /// <summary>RE-24's quaternion product: w = ((aW*bW - aX*bX) - aY*bY) - aZ*bZ and so on, W, X, Y, Z.</summary>
    public static float[] Mul(float[] a, float[] b)
    {
        var w = ((a[0] * b[0] - a[1] * b[1]) - a[2] * b[2]) - a[3] * b[3];
        var x = ((a[0] * b[1] + a[1] * b[0]) + a[2] * b[3]) - a[3] * b[2];
        var y = ((a[0] * b[2] + a[2] * b[0]) + a[3] * b[1]) - a[1] * b[3];
        var z = ((a[0] * b[3] + a[3] * b[0]) + a[1] * b[2]) - a[2] * b[1];
        return [w, x, y, z];
    }

    /// <summary>RE-24's Conj: (W, -X, -Y, -Z).</summary>
    public static float[] Conj(float[] q)
    {
        return [q[0], -q[1], -q[2], -q[3]];
    }

    /// <summary>RE-24's Log: (0, f*x, f*y, f*z) with the float acos and the 0.001f threshold.</summary>
    public static float[] Log(float[] q)
    {
        var angle = q[0] <= -1f ? PiF : q[0] >= 1f ? 0f : (float)Math.Acos((double)q[0]);
        var s = (float)Math.Sin((double)angle);
        var f = MathF.Abs(s) >= EpsilonF ? angle / s : 1f;
        return [0f, f * q[1], f * q[2], f * q[3]];
    }

    /// <summary>RE-24's Exp: (cos, f*x, f*y, f*z) with angle = sqrt((x*x + y*y) + z*z) and the 0.001f threshold.</summary>
    public static float[] Exp(float[] v)
    {
        var angle = MathF.Sqrt((v[1] * v[1] + v[2] * v[2]) + v[3] * v[3]);
        var c = (float)Math.Cos((double)angle);
        var s = (float)Math.Sin((double)angle);
        var f = MathF.Abs(s) >= EpsilonF ? s / angle : 1f;
        return [c, f * v[1], f * v[2], f * v[3]];
    }

    /// <summary>
    ///     RE-24 step 2 for a TBC quaternion group of two or more keys already chain-aligned and W-clamped: each key's
    ///     incoming (In) and outgoing (Out) inner points.
    /// </summary>
    /// <param name="times">The key times.</param>
    /// <param name="keys">The aligned keys, W, X, Y, Z.</param>
    /// <param name="tension">Each key's first stored TBC float.</param>
    /// <param name="continuity">Each key's second stored TBC float.</param>
    /// <param name="bias">Each key's third stored TBC float.</param>
    /// <returns>The inner points, W, X, Y, Z.</returns>
    public static (float[][] In, float[][] Out) TbcInnerPoints(float[] times, float[][] keys, float[] tension,
        float[] continuity, float[] bias)
    {
        var n = keys.Length;
        var incoming = new float[n][];
        var outgoing = new float[n][];
        for (var i = 0; i < n; i++)
        {
            var p = i == 0 ? 0 : i - 1;
            var x = i == n - 1 ? n - 1 : i + 1;
            var l1 = Log(Mul(Conj(keys[p]), keys[i]));
            var l2 = Log(Mul(Conj(keys[i]), keys[x]));
            var inv = 1f / (times[x] - times[p]);
            var omT = 1f - tension[i];
            var omC = 1f - continuity[i];
            var opC = 1f + continuity[i];
            var omB = 1f - bias[i];
            var opB = 1f + bias[i];
            var a = (times[i] - times[p]) * inv;
            var aT = a * omT;
            var c1 = (aT * opC) * opB;
            var c2 = (aT * omC) * omB;
            var dd = new float[4];
            var ddHalf = new float[4];
            for (var k = 0; k < 4; k++)
            {
                dd[k] = c2 * l2[k] + c1 * l1[k];
                ddHalf[k] = 0.5f * (dd[k] - l2[k]);
            }

            outgoing[i] = Mul(keys[i], Exp(ddHalf));
            var b = (times[x] - times[i]) * inv;
            var bT = b * omT;
            var c3 = (bT * omC) * opB;
            var c4 = (bT * opC) * omB;
            var dsHalf = new float[4];
            for (var k = 0; k < 4; k++)
            {
                var ds = c4 * l2[k] + c3 * l1[k];
                dsHalf[k] = 0.5f * (l1[k] - ds);
            }

            incoming[i] = Mul(keys[i], Exp(dsHalf));
        }

        return (incoming, outgoing);
    }

    /// <summary>
    ///     RE-24 step 2 for a QUADRATIC quaternion group of two or more aligned keys: s[i] = q[i] * Exp(-0.25 * (Log(conj
    ///     q[i] * q[p]) + Log(conj q[i] * q[x]))), the sum formed per component first; the same point is incoming and
    ///     outgoing.
    /// </summary>
    public static float[][] QuadraticInnerPoints(float[][] keys)
    {
        var n = keys.Length;
        var points = new float[n][];
        for (var i = 0; i < n; i++)
        {
            var p = i == 0 ? 0 : i - 1;
            var x = i == n - 1 ? n - 1 : i + 1;
            var toPrevious = Log(Mul(Conj(keys[i]), keys[p]));
            var toNext = Log(Mul(Conj(keys[i]), keys[x]));
            var scaled = new float[4];
            for (var k = 0; k < 4; k++)
            {
                scaled[k] = -0.25f * (toPrevious[k] + toNext[k]);
            }

            points[i] = Mul(keys[i], Exp(scaled));
        }

        return points;
    }

    /// <summary>
    ///     RE-17's engine interpolation between two keys (NiQuaternion::Slerp): the Float32 dot in W, X, Y, Z order, the
    ///     counter-warped parameter, r = p + t'(q - p) per component, no sign test; returned unnormalized (W, X, Y, Z).
    /// </summary>
    /// <param name="p">The first key, W, X, Y, Z.</param>
    /// <param name="q">The second key, W, X, Y, Z (no shortest-path flip).</param>
    /// <param name="t">The segment fraction.</param>
    /// <returns>The component interpolation before normalization.</returns>
    public static float[] CounterWarpedComponents(float[] p, float[] q, float t)
    {
        var d = ((p[0] * q[0] + p[1] * q[1]) + p[2] * q[2]) + p[3] * q[3];
        var warped = t <= 0.5f ? CounterWarp(t, d) : 1f - CounterWarp(1f - t, d);
        return
        [
            p[0] + warped * (q[0] - p[0]), p[1] + warped * (q[1] - p[1]), p[2] + warped * (q[2] - p[2]),
            p[3] + warped * (q[3] - p[3])
        ];
    }

    /// <summary>
    ///     RE-22 rule 3: the track clock of a free-running controller from the probe's controller facts, or null with the
    ///     reason when the rule gives none (inactive, double sentinel, one sentinel, undefined cycle, stop before start).
    /// </summary>
    /// <param name="frequencyBits">The stored frequency bits.</param>
    /// <param name="phaseBits">The stored phase bits.</param>
    /// <param name="startBits">The stored start-time bits.</param>
    /// <param name="stopBits">The stored stop-time bits.</param>
    /// <param name="cycleName">The probe's cycle name (LOOP, REVERSE or CLAMP).</param>
    /// <param name="active">The active flag.</param>
    /// <param name="playBackwards">The play-backwards flag.</param>
    /// <param name="reason">Why no clock, or null.</param>
    /// <returns>(frequency, phase, start, stop, cycle), or null.</returns>
    public static (float Frequency, float Phase, float Start, float Stop, SceneAnimationCycle Cycle)? ControllerClock(
        uint frequencyBits, uint phaseBits, uint startBits, uint stopBits, string? cycleName, bool active,
        bool playBackwards, out string? reason)
    {
        reason = null;
        if (!active)
        {
            reason = "inactive";
            return null;
        }

        var startSentinel = startBits == StartSentinelBits;
        var stopSentinel = stopBits == StopSentinelBits;
        if (startSentinel && stopSentinel)
        {
            reason = "double sentinel";
            return null;
        }

        var frequency = BitConverter.UInt32BitsToSingle(frequencyBits);
        var phase = BitConverter.UInt32BitsToSingle(phaseBits);
        var start = BitConverter.UInt32BitsToSingle(startBits);
        var stop = BitConverter.UInt32BitsToSingle(stopBits);
        SceneAnimationCycle cycle;
        switch (cycleName)
        {
            case "LOOP":
                cycle = SceneAnimationCycle.Loop;
                break;
            case "REVERSE":
                cycle = SceneAnimationCycle.Reverse;
                break;
            case "CLAMP":
                cycle = SceneAnimationCycle.Clamp;
                break;
            default:
                reason = $"undefined cycle {cycleName}";
                return null;
        }

        if (startSentinel || stopSentinel || !float.IsFinite(frequency) || !float.IsFinite(phase) ||
            !float.IsFinite(start) || !float.IsFinite(stop) || stop < start)
        {
            reason = "degenerate clock";
            return null;
        }

        if (cycle == SceneAnimationCycle.Reverse)
        {
            phase = (float)((double)phase + (playBackwards ? stop : start));
        }
        else if (playBackwards)
        {
            frequency = -frequency;
            phase = (float)((double)start + stop - phase);
        }

        if (!float.IsFinite(phase))
        {
            reason = "degenerate clock";
            return null;
        }

        return (frequency, phase, start, stop, cycle);
    }

    /// <summary>RE-17's CounterWarp: t * (k + (1 + (k*t) * ((t + t) - 3))), k = SLOPE * (1 - ATTEN*d)^2.</summary>
    private static float CounterWarp(float t, float d)
    {
        var f = 1f - Attenuation * d;
        f *= f;
        var k = Slope * f;
        return t * (k + (1f + (k * t) * ((t + t) - 3f)));
    }
}

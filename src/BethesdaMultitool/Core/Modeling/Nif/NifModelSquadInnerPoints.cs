namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     RE-24 step (2) (docs/formats/nif-animation-engine-behavior-20260925.md): the engine's inner Squad points of a
///     quaternion TBC key group (NiTCBRotKey::CalculateDVals: an outgoing point A and an incoming point B per key) or of
///     a QUADRATIC one (NiBezRotKey::FillDerivedVals: one point s per key, NiQuaternion::Intermediate with self
///     neighbours at the ends, used as both), computed in Float32 exactly as the GECK at x87 precision 24 and the X360
///     scalar code do. Pure.
/// </summary>
/// <remarks>
///     <para>
///         Every operation is a float operation on float locals in the rule's order: no double intermediate, no fused
///         multiply-add (RyuJIT never contracts a product and a sum), <see cref="MathF.Sqrt" /> (correctly rounded) for
///         Exp's angle, and acos, sin and cos evaluated as <c>(float)Math.Acos/Sin/Cos((double)x)</c>, the C runtime's
///         double result rounded to float once, which is what both builds do (<see cref="MathF.Acos" />, <see cref="MathF.Sin" />
///         and <see cref="MathF.Cos" /> compute in float and are not the engine). With that, the results reproduce the
///         RE-24 receipts bit for bit (sneak2hhattackspin.kf block 85 A_0 and B_1; h2haim.kf block 17 A_0, B_1 and B_last).
///     </para>
///     <para>
///         The keys must already be the engine's load-time keys: RE-17 chain sign alignment and W clamp applied
///         (<see cref="BethesdaMultitool.Core.Formats.Nif.Rendering.Animation.NifRotationKeyEngineRule.AlignChainAndClampW" />),
///         NOT normalized (pre-normalized keys match only 67,215 of 92,423 retail samples). A group of one key has no
///         inner points: the engine's fill returns before computing any, and the caller holds the raw key.
///     </para>
///     <para>
///         Helpers, in the rule's notation: Mul(a, b) is NiQuaternion::operator* with
///         w = ((aW*bW - aX*bX) - aY*bY) - aZ*bZ, x = ((aW*bX + aX*bW) + aY*bZ) - aZ*bY,
///         y = ((aW*bY + aY*bW) + aZ*bX) - aX*bZ, z = ((aW*bZ + aZ*bW) + aX*bY) - aY*bX; Log(q) returns
///         (0, f*x, f*y, f*z) with the angle pi_f when W is at most -1, 0 when W is at least 1, otherwise acos(W), and
///         f = angle / sin(angle) when |sin| is at least 0.001f, else 1; Exp(v) returns (cos, f*x, f*y, f*z) with the
///         angle sqrt((x*x + y*y) + z*z) and f = sin / angle under the same threshold. The constants are the bits the
///         builds load: PI_F 0x40490FDB, EPS_F 0x3A83126F.
///     </para>
/// </remarks>
internal static class NifModelSquadInnerPoints
{
    /// <summary>The bits of the engine's pi (X360 [82141db4], GECK [dbca2c]).</summary>
    public const uint PiBits = 0x40490FDB;

    /// <summary>The bits of the engine's 0.001f sine threshold (X360 [82141db0], GECK [dbca28]).</summary>
    public const uint EpsilonBits = 0x3A83126F;

    private static readonly float PiF = BitConverter.UInt32BitsToSingle(PiBits);
    private static readonly float EpsilonF = BitConverter.UInt32BitsToSingle(EpsilonBits);

    /// <summary>NiQuaternion::operator* (X360 0x82d74028, GECK 0x7e3ba0), the Hamilton product a times b.</summary>
    /// <param name="a">The left factor (the object).</param>
    /// <param name="b">The right factor (the argument).</param>
    /// <returns>The product, every product and sum rounded to Float32 in the engine's order.</returns>
    public static NifModelSquadQuaternion Multiply(in NifModelSquadQuaternion a, in NifModelSquadQuaternion b)
    {
        var ww = a.W * b.W;
        var xx = a.X * b.X;
        var yy = a.Y * b.Y;
        var zz = a.Z * b.Z;
        var w = ww - xx;
        w -= yy;
        w -= zz;

        var wx = a.W * b.X;
        var xw = a.X * b.W;
        var yz = a.Y * b.Z;
        var zy = a.Z * b.Y;
        var x = wx + xw;
        x += yz;
        x -= zy;

        var wy = a.W * b.Y;
        var yw = a.Y * b.W;
        var zx = a.Z * b.X;
        var xz = a.X * b.Z;
        var y = wy + yw;
        y += zx;
        y -= xz;

        var wz = a.W * b.Z;
        var zw = a.Z * b.W;
        var xy = a.X * b.Y;
        var yx = a.Y * b.X;
        var z = wz + zw;
        z += xy;
        z -= yx;
        return new NifModelSquadQuaternion(w, x, y, z);
    }

    /// <summary>NiQuaternion::Log (X360 0x82e465f8, GECK 0x827530).</summary>
    /// <param name="q">A quaternion (its W decides the angle; the length is not examined).</param>
    /// <returns>(0, f*x, f*y, f*z), see the type remarks.</returns>
    public static NifModelSquadQuaternion Log(in NifModelSquadQuaternion q)
    {
        float angle;
        if (q.W <= -1f)
        {
            angle = PiF;
        }
        else if (q.W >= 1f)
        {
            angle = 0f;
        }
        else
        {
            angle = (float)Math.Acos((double)q.W);
        }

        var sine = (float)Math.Sin((double)angle);
        var factor = MathF.Abs(sine) >= EpsilonF ? angle / sine : 1f;
        return new NifModelSquadQuaternion(0f, factor * q.X, factor * q.Y, factor * q.Z);
    }

    /// <summary>NiQuaternion::Exp (X360 0x82e935d0, GECK 0x838110); the argument's W is ignored.</summary>
    /// <param name="v">A pure vector quaternion (0, x, y, z).</param>
    /// <returns>(cos, f*x, f*y, f*z), see the type remarks.</returns>
    public static NifModelSquadQuaternion Exp(in NifModelSquadQuaternion v)
    {
        var xx = v.X * v.X;
        var yy = v.Y * v.Y;
        var zz = v.Z * v.Z;
        var squared = xx + yy;
        squared += zz;
        var angle = MathF.Sqrt(squared);
        var cosine = (float)Math.Cos((double)angle);
        var sine = (float)Math.Sin((double)angle);
        var factor = MathF.Abs(sine) >= EpsilonF ? sine / angle : 1f;
        return new NifModelSquadQuaternion(cosine, factor * v.X, factor * v.Y, factor * v.Z);
    }

    /// <summary>
    ///     NiTCBRotKey::FillDerivedVals after the chain flip (X360 0x82d738b0, GECK 0x7e3fb0): CalculateDVals for every
    ///     key i with p = (i == 0 ? 0 : i - 1) and x = (i == n - 1 ? n - 1 : i + 1), the endpoints taking themselves as
    ///     their missing neighbour (which gives RE-19's A_0 = q0 Exp(-L/2) and B_last = q Exp(+L/2)).
    /// </summary>
    /// <param name="times">The key times, strictly increasing (so no span p..x is empty).</param>
    /// <param name="keys">The load-time keys (chain aligned, W clamped, not normalized), one per time.</param>
    /// <param name="tension">Each key's tension (its first stored TBC float).</param>
    /// <param name="continuity">Each key's continuity (its second stored TBC float).</param>
    /// <param name="bias">Each key's bias (its third stored TBC float).</param>
    /// <returns>Per key, the incoming point B and the outgoing point A.</returns>
    /// <exception cref="ArgumentException">The spans differ in length, fewer than two keys are given, or a span p..x is zero-length.</exception>
    public static (NifModelSquadQuaternion[] Incoming, NifModelSquadQuaternion[] Outgoing) Tbc(
        ReadOnlySpan<float> times,
        ReadOnlySpan<NifModelSquadQuaternion> keys,
        ReadOnlySpan<float> tension,
        ReadOnlySpan<float> continuity,
        ReadOnlySpan<float> bias)
    {
        var count = times.Length;
        if (keys.Length != count || tension.Length != count || continuity.Length != count || bias.Length != count)
        {
            throw new ArgumentException("The times, keys and TBC parameters must have one element per key.");
        }

        if (count < 2)
        {
            throw new ArgumentException("The engine computes inner points only for two or more keys.", nameof(times));
        }

        var incoming = new NifModelSquadQuaternion[count];
        var outgoing = new NifModelSquadQuaternion[count];
        for (var i = 0; i < count; i++)
        {
            var p = i == 0 ? 0 : i - 1;
            var x = i == count - 1 ? count - 1 : i + 1;
            var span = times[x] - times[p];
            if (span == 0f)
            {
                throw new ArgumentException("A zero-length span makes the engine's inner point NaN.", nameof(times));
            }

            var l1 = Log(Multiply(keys[p].Conjugate(), keys[i]));
            var l2 = Log(Multiply(keys[i].Conjugate(), keys[x]));
            var inverse = 1f / span;
            var omT = 1f - tension[i];
            var omC = 1f - continuity[i];
            var opC = 1f + continuity[i];
            var omB = 1f - bias[i];
            var opB = 1f + bias[i];

            var before = times[i] - times[p];
            var a = before * inverse;
            var aT = a * omT;
            var c1 = aT * opC;
            c1 *= opB;
            var c2 = aT * omC;
            c2 *= omB;
            var dd = Blend(c2, l2, c1, l1);
            outgoing[i] = Multiply(keys[i], Exp(HalfDifference(dd, l2)));

            var after = times[x] - times[i];
            var b = after * inverse;
            var bT = b * omT;
            var c3 = bT * omC;
            c3 *= opB;
            var c4 = bT * opC;
            c4 *= omB;
            var ds = Blend(c4, l2, c3, l1);
            incoming[i] = Multiply(keys[i], Exp(HalfDifference(l1, ds)));
        }

        return (incoming, outgoing);
    }

    /// <summary>
    ///     NiBezRotKey::FillDerivedVals after the chain flip (X360 0x82d6b1a8, GECK 0x7e11d0): s_i =
    ///     Intermediate(q_p, q_i, q_x) = q_i Exp(-0.25 (Log(q_i^-1 q_p) + Log(q_i^-1 q_x))), the per-component sum formed
    ///     first, with self neighbours at the ends; the same point serves as the key's incoming and outgoing point. It is
    ///     not TBC with zero parameters (that reading matches 12 and 11 of 575 synthetic points).
    /// </summary>
    /// <param name="keys">The load-time keys (chain aligned, W clamped, not normalized).</param>
    /// <returns>One inner point per key.</returns>
    /// <exception cref="ArgumentException">Fewer than two keys are given.</exception>
    public static NifModelSquadQuaternion[] Quadratic(ReadOnlySpan<NifModelSquadQuaternion> keys)
    {
        var count = keys.Length;
        if (count < 2)
        {
            throw new ArgumentException("The engine computes inner points only for two or more keys.", nameof(keys));
        }

        var points = new NifModelSquadQuaternion[count];
        for (var i = 0; i < count; i++)
        {
            var p = i == 0 ? 0 : i - 1;
            var x = i == count - 1 ? count - 1 : i + 1;
            var inverse = keys[i].Conjugate();
            var toPrevious = Log(Multiply(inverse, keys[p]));
            var toNext = Log(Multiply(inverse, keys[x]));
            var sumX = toPrevious.X + toNext.X;
            var sumY = toPrevious.Y + toNext.Y;
            var sumZ = toPrevious.Z + toNext.Z;
            var sumW = toPrevious.W + toNext.W;
            var scaled = new NifModelSquadQuaternion(-0.25f * sumW, -0.25f * sumX, -0.25f * sumY, -0.25f * sumZ);
            points[i] = Multiply(keys[i], Exp(scaled));
        }

        return points;
    }

    /// <summary>Per component, (first * a) + (second * b), each product and the sum rounded to Float32.</summary>
    private static NifModelSquadQuaternion Blend(
        float first, in NifModelSquadQuaternion a, float second, in NifModelSquadQuaternion b)
    {
        var w1 = first * a.W;
        var w2 = second * b.W;
        var x1 = first * a.X;
        var x2 = second * b.X;
        var y1 = first * a.Y;
        var y2 = second * b.Y;
        var z1 = first * a.Z;
        var z2 = second * b.Z;
        return new NifModelSquadQuaternion(w1 + w2, x1 + x2, y1 + y2, z1 + z2);
    }

    /// <summary>Per component, 0.5f * (a - b), the difference rounded before the product.</summary>
    private static NifModelSquadQuaternion HalfDifference(in NifModelSquadQuaternion a, in NifModelSquadQuaternion b)
    {
        var w = a.W - b.W;
        var x = a.X - b.X;
        var y = a.Y - b.Y;
        var z = a.Z - b.Z;
        return new NifModelSquadQuaternion(0.5f * w, 0.5f * x, 0.5f * y, 0.5f * z);
    }
}

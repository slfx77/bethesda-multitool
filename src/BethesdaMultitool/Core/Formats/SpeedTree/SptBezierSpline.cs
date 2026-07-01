using System.Globalization;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     A SpeedTree branch/frond spline, stored in the <c>.spt</c> as a text string of the form
///     <c>"BezierSpline &lt;x&gt; &lt;y&gt; &lt;z&gt; { &lt;N&gt; &lt;N×5 floats&gt; }"</c>
///     (parsed by FUN_008E1220 in Geck.exe). The three header floats are a reference vector; each
///     control point is five floats <c>[param, a, b, c, d]</c> where <c>param</c> runs 0..1 along
///     the spline. The precise role of each of a branch's nine splines (3D path vs. radius / roll /
///     gnarl profile) is determined empirically during geometry generation.
/// </summary>
public sealed record SptBezierSpline
{
    /// <summary>The three header floats <c>(MIN, MAX, VARIANCE)</c> that scale the normalized control-point curve.</summary>
    public Vector3 Header { get; init; }

    public IReadOnlyList<SptSplineControlPoint> ControlPoints { get; init; } = [];

    /// <summary>Size of the engine's precomputed curve LUT (<c>CIdvBezierSpline::CreateEvenlySpacedPoints</c>
    /// builds exactly 500 evenly-spaced points; <c>Evaluate</c>/<c>ScaledVariance</c> assert the count is 500).</summary>
    private const int LutSamples = 500;

    /// <summary>Lazily-built, x-reparameterized 500-entry curve LUT (the engine's <c>+0x3c</c> array). Cached;
    /// safe as a mutable field because no <see cref="SptBezierSpline" /> equality / <c>with</c> is used anywhere.</summary>
    private float[]? _lut;

    /// <summary>
    ///     Evaluate the spline at <paramref name="param" /> (0..1) exactly as the SDK's
    ///     <c>CIdvBezierSpline::Evaluate</c> does (decompiled, L131): <c>Header.X + lut(param)·(Header.Y −
    ///     Header.X) + random(−Header.Z, +Header.Z)</c>, where <c>lut(param)</c> reads the 500-entry curve LUT
    ///     at a TRUNCATED index with linear interpolation (<see cref="LutInterp" />). The three header floats
    ///     are <c>(MIN, MAX, VARIANCE)</c>. <paramref name="random" /> is a <c>(min,max)→value</c> uniform RNG;
    ///     pass null to omit the variance term (deterministic mean).
    /// </summary>
    public float Evaluate(float param, Func<float, float, float>? random = null, bool forceDraw = false)
    {
        // When MIN == MAX the curve term is multiplied by zero, so skip the (relatively expensive) curve
        // evaluation entirely — the common case for length/radius/most profile splines.
        var span = Header.Y - Header.X;
        var value = MathF.Abs(span) > 1e-9f ? Header.X + LutInterp(param) * span : Header.X;

        // The engine's spline evaluator calls GetUniform(-Z, +Z) UNCONDITIONALLY — even when the variance Z
        // is 0, returning 0 but still ADVANCING the shared RNG. `forceDraw` reproduces that for the per-ring
        // loft evals (the draw count must match draw-for-draw or the whole downstream uniform stream desyncs).
        if (random is not null && (forceDraw || MathF.Abs(Header.Z) > 1e-9f))
        {
            value += random(-Header.Z, Header.Z);
        }

        return value;
    }

    /// <summary>
    ///     Curve-modulated noise, mirroring the SDK's <c>CIdvBezierSpline::ScaledVariance</c> (decompiled,
    ///     L469): <c>random(−Header.Z·lut, +Header.Z·lut)</c> where <c>lut</c> reads the SAME 500-entry LUT
    ///     but at a ROUNDED index with NO interpolation (<see cref="LutNearest" />) — distinct from
    ///     <see cref="Evaluate" />'s truncate+interp. Always draws (advances the shared RNG even at Z·lut = 0).
    /// </summary>
    public float ScaledVariance(float param, Func<float, float, float> random)
    {
        var scaled = Header.Z * LutNearest(param);
        return random(-scaled, scaled);
    }

    /// <summary>The normalized 0..1 control-point curve at <paramref name="param" /> — the
    /// <see cref="Evaluate" />-style truncate+interp LUT read (kept for callers/tests that want the bare curve).</summary>
    public float Curve(float param) => LutInterp(param);

    /// <summary>Read the curve LUT at a TRUNCATED index with linear interpolation, matching
    /// <c>CIdvBezierSpline::Evaluate</c> (L146-154): <c>i = (int)(param·499)</c>, lerp <c>lut[i]..lut[i+1]</c>
    /// by the fractional part.</summary>
    private float LutInterp(float param)
    {
        var lut = Lut();
        if (lut is null)
        {
            return Math.Clamp(param, 0f, 1f);
        }

        var fp = Math.Clamp(param, 0f, 1f) * (LutSamples - 1);
        var i = (int)fp;
        if (i >= LutSamples - 1)
        {
            return lut[LutSamples - 1];
        }

        return lut[i] + (lut[i + 1] - lut[i]) * (fp - i);
    }

    /// <summary>Read the curve LUT at the NEAREST index (no interpolation), matching
    /// <c>CIdvBezierSpline::ScaledVariance</c> (L481): <c>lut[(int)(param·499 + 0.5)]</c>.</summary>
    private float LutNearest(float param)
    {
        var lut = Lut();
        if (lut is null)
        {
            return Math.Clamp(param, 0f, 1f);
        }

        var i = (int)(Math.Clamp(param, 0f, 1f) * (LutSamples - 1) + 0.5f);
        return lut[Math.Min(i, LutSamples - 1)];
    }

    /// <summary>
    ///     Build the engine's 500-entry curve LUT exactly as <c>CIdvBezierSpline::CreateEvenlySpacedPoints</c>
    ///     (decompiled, L518): (1) sample the raw cubic Bézier at evenly-spaced PARAMETER <c>t = i/500</c>
    ///     (<c>EvaluateRawPoint</c> → <c>SplineInterpolate</c>, a de Casteljau cubic over Hermite handles
    ///     <c>anchor ± normalize(B,C)·D</c>), giving (x, y) pairs; (2) REPARAMETERIZE BY X — for each grid
    ///     point <c>target = i/500</c> bracket the raw samples by x and lerp y (L645-665). Endpoints are the
    ///     first/last control-point A values (L610-637). Returns null for &lt;2 control points (degenerate;
    ///     callers fall back to identity), constant for 1.
    /// </summary>
    private float[]? Lut()
    {
        if (_lut is not null)
        {
            return _lut;
        }

        var cps = ControlPoints;
        var n = cps.Count;
        if (n < 2)
        {
            if (n == 1)
            {
                var flat = new float[LutSamples];
                Array.Fill(flat, cps[0].A);
                return _lut = flat;
            }

            return null;
        }

        // 1) Raw cubic samples at t = i/500 (engine samples i/N, NOT i/(N-1) — t maxes at 499/500).
        Span<float> rawX = stackalloc float[LutSamples];
        Span<float> rawY = stackalloc float[LutSamples];
        for (var i = 0; i < LutSamples; i++)
        {
            var (x, y) = RawPoint(cps, n, i / (float)LutSamples);
            rawX[i] = x;
            rawY[i] = y;
        }

        // 2) Reparameterize by x onto an even grid. Endpoints copy the control-point A values directly.
        var lut = new float[LutSamples];
        lut[0] = cps[0].A;
        lut[LutSamples - 1] = cps[n - 1].A;
        var k = 0;
        for (var i = 1; i < LutSamples - 1; i++)
        {
            var target = i / (float)LutSamples;
            while (k < LutSamples - 1 && !(rawX[k] <= target && target < rawX[k + 1]))
            {
                k++;
            }

            if (k >= LutSamples - 1)
            {
                lut[i] = rawY[LutSamples - 1];
                k = LutSamples - 2;
                continue;
            }

            var dx = rawX[k + 1] - rawX[k];
            var frac = dx > 1e-9f ? (target - rawX[k]) / dx : 0f;
            lut[i] = rawY[k] + (rawY[k + 1] - rawY[k]) * frac;
        }

        return _lut = lut;
    }

    /// <summary>
    ///     One raw cubic-Bézier point at parameter <paramref name="t" /> (0..1) over the control
    ///     polygon, mirroring <c>EvaluateRawPoint</c>+<c>SplineInterpolate</c> (cubic de Casteljau).
    ///     The control point is a Hermite node: anchor <c>(Param, A)</c>, UNIT tangent direction
    ///     <c>(B, C)</c>, and handle length <c>D</c>. <c>CIdvBezierSpline::AddControlPoint</c> (L661-668)
    ///     derives the Bézier handles as <c>anchor ± tangent·D</c> — the outgoing handle of point k is
    ///     <c>anchor_k + tangent_k·D_k</c>, the incoming handle of k+1 is <c>anchor_{k+1} −
    ///     tangent_{k+1}·D_{k+1}</c>. (B,C) is a direction, NOT a handle position — using it raw flattens
    ///     every shaped curve, e.g. collapsing the pine's tapering length curve into a uniform column.
    /// </summary>
    private static (float X, float Y) RawPoint(IReadOnlyList<SptSplineControlPoint> cps, int n, float t)
    {
        var ft = t * (n - 1);
        var k = Math.Clamp((int)ft, 0, n - 2);
        var lt = ft - k;

        var c0 = cps[k];
        var c1 = cps[k + 1];
        var p0 = new Vector2(c0.Param, c0.A);
        var p3 = new Vector2(c1.Param, c1.A);
        var p1 = p0 + NormalizeOrZero(new Vector2(c0.B, c0.C)) * c0.D; // outgoing handle of k
        var p2 = p3 - NormalizeOrZero(new Vector2(c1.B, c1.C)) * c1.D; // incoming handle of k+1

        var a = Vector2.Lerp(p0, p1, lt);
        var b = Vector2.Lerp(p1, p2, lt);
        var c = Vector2.Lerp(p2, p3, lt);
        var d = Vector2.Lerp(a, b, lt);
        var e = Vector2.Lerp(b, c, lt);
        var r = Vector2.Lerp(d, e, lt);
        return (r.X, r.Y);
    }

    private static Vector2 NormalizeOrZero(Vector2 v)
    {
        var lenSq = v.LengthSquared();
        return lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : Vector2.Zero;
    }

    /// <summary>
    ///     Parse a <c>BezierSpline</c> text string. Returns null if the text is not a well-formed
    ///     BezierSpline definition (mirrors FUN_008E1220, which silently ignores non-matching strings).
    /// </summary>
    public static SptBezierSpline? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 5 || !string.Equals(tokens[0], "BezierSpline", StringComparison.Ordinal))
        {
            return null;
        }

        var index = 1;
        if (!TryFloat(tokens, ref index, out var hx) ||
            !TryFloat(tokens, ref index, out var hy) ||
            !TryFloat(tokens, ref index, out var hz))
        {
            return null;
        }

        if (index >= tokens.Length || tokens[index] != "{")
        {
            return null;
        }

        index++;

        if (!TryInt(tokens, ref index, out var count) || count < 0)
        {
            return null;
        }

        var points = new SptSplineControlPoint[count];
        for (var i = 0; i < count; i++)
        {
            if (!TryFloat(tokens, ref index, out var p) ||
                !TryFloat(tokens, ref index, out var a) ||
                !TryFloat(tokens, ref index, out var b) ||
                !TryFloat(tokens, ref index, out var c) ||
                !TryFloat(tokens, ref index, out var d))
            {
                return null;
            }

            points[i] = new SptSplineControlPoint(p, a, b, c, d);
        }

        return new SptBezierSpline { Header = new Vector3(hx, hy, hz), ControlPoints = points };
    }

    private static bool TryFloat(string[] tokens, ref int index, out float value)
    {
        if (index < tokens.Length &&
            float.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            index++;
            return true;
        }

        value = 0f;
        return false;
    }

    private static bool TryInt(string[] tokens, ref int index, out int value)
    {
        if (index < tokens.Length &&
            int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            index++;
            return true;
        }

        value = 0;
        return false;
    }
}

/// <summary>
///     One BezierSpline control point: a parameter (0..1 along the spline) plus four payload floats
///     whose meaning depends on the spline's role.
/// </summary>
public readonly record struct SptSplineControlPoint(float Param, float A, float B, float C, float D);

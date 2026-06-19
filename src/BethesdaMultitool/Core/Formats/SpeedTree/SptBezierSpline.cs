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
    public Vector3 Header { get; init; }

    public IReadOnlyList<SptSplineControlPoint> ControlPoints { get; init; } = [];

    /// <summary>
    ///     Evaluate the spline at <paramref name="param" /> (0..1) exactly as the SDK's
    ///     <c>CIdvBezierSpline::Evaluate</c> does (decompiled from the Xbox MemDebug PE):
    ///     <c>Header.X + curve(param)·(Header.Y − Header.X) + random(−Header.Z, +Header.Z)</c>.
    ///     The three header floats are <c>(MIN, MAX, VARIANCE)</c>; <see cref="Curve" /> is the
    ///     normalized 0..1 control-point curve. <paramref name="random" /> is a <c>(min,max)→value</c>
    ///     uniform RNG; pass null to omit the variance term (deterministic mean).
    /// </summary>
    public float Evaluate(float param, Func<float, float, float>? random = null)
    {
        // When MIN == MAX the curve term is multiplied by zero, so skip the (relatively expensive)
        // curve evaluation entirely — the common case for length/radius/most profile splines.
        var span = Header.Y - Header.X;
        var value = MathF.Abs(span) > 1e-9f ? Header.X + Curve(param) * span : Header.X;
        if (random is not null && MathF.Abs(Header.Z) > 1e-9f)
        {
            value += random(-Header.Z, Header.Z);
        }

        return value;
    }

    /// <summary>
    ///     Curve-modulated noise, mirroring the SDK's <c>CIdvBezierSpline::ScaledVariance</c>:
    ///     <c>random(−Header.Z·curve(param), +Header.Z·curve(param))</c>. Returns 0 when the spline
    ///     has no variance.
    /// </summary>
    public float ScaledVariance(float param, Func<float, float, float> random)
    {
        if (MathF.Abs(Header.Z) <= 1e-9f)
        {
            return 0f;
        }

        var scaled = Header.Z * Curve(param);
        return random(-scaled, scaled);
    }

    /// <summary>
    ///     The control-point curve <c>y(param)</c>, evaluated exactly as the SDK's
    ///     <c>CIdvBezierSpline</c> 500-sample LUT does (decompiled <c>CreateEvenlySpacedPoints</c> →
    ///     <c>EvaluateRawPoint</c> → <c>SplineInterpolate</c>): a cubic Bézier through the control
    ///     points — on-curve point <c>(Param, A)</c>, handle <c>(B, C)</c> — reparameterized so
    ///     <paramref name="param" /> indexes the curve's x (parameter) axis, then the stored y is read
    ///     back with linear interpolation. Identity when there are no control points; constant for one.
    /// </summary>
    public float Curve(float param)
    {
        var cps = ControlPoints;
        if (cps.Count == 0)
        {
            return Math.Clamp(param, 0f, 1f);
        }

        if (cps.Count == 1)
        {
            return cps[0].A;
        }

        var p = Math.Clamp(param, 0f, 1f);
        var n = cps.Count;

        // 1) Sample the cubic Bézier polyline at evenly-spaced parameter t (CreateEvenlySpacedPoints'
        //    first pass / EvaluateRawPoint+SplineInterpolate). Each sample is (x, y): x = the spline's
        //    parameter axis (control-point Param), y = its value (A).
        const int samples = 500;
        Span<float> sx = stackalloc float[samples];
        Span<float> sy = stackalloc float[samples];
        for (var i = 0; i < samples; i++)
        {
            var (x, y) = RawPoint(cps, n, i / (float)(samples - 1));
            sx[i] = x;
            sy[i] = y;
        }

        // 2) Read y back at x = p (the SDK reparameterizes so the LUT index maps to the param axis;
        //    decompiled L566-586 brackets by x and lerps the stored value). The samples advance
        //    monotonically in x for the well-behaved control polygons SpeedTree authors use.
        if (p <= sx[0])
        {
            return sy[0];
        }

        for (var i = 1; i < samples; i++)
        {
            if (p <= sx[i])
            {
                var dx = sx[i] - sx[i - 1];
                var t = dx > 1e-9f ? (p - sx[i - 1]) / dx : 0f;
                return sy[i - 1] + (sy[i] - sy[i - 1]) * t;
            }
        }

        return sy[samples - 1];
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

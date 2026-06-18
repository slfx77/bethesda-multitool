using System.Globalization;
using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.SpeedTree;

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
        var value = Header.X + Curve(param) * (Header.Y - Header.X);
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
    ///     The normalized control-point curve: the value (control-point <c>A</c>=f2) as a function of
    ///     the parameter (control-point <c>Param</c>=f1), with the control points treated as on-curve
    ///     anchors and smoothstep interpolation between them. Identity when there are no control points.
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

        // Anchors are on-curve points (x = Param/f1, y = A/f2), stored in arbitrary x-order. Bracket p
        // between the nearest anchor at-or-below it (lo) and at-or-above it (hi), then smoothstep.
        var p = Math.Clamp(param, 0f, 1f);
        float loX = float.NegativeInfinity, hiX = float.PositiveInfinity, loY = 0f, hiY = 0f;
        bool haveLo = false, haveHi = false;
        foreach (var cp in cps)
        {
            if (cp.Param <= p && cp.Param > loX)
            {
                loX = cp.Param;
                loY = cp.A;
                haveLo = true;
            }

            if (cp.Param >= p && cp.Param < hiX)
            {
                hiX = cp.Param;
                hiY = cp.A;
                haveHi = true;
            }
        }

        if (!haveLo)
        {
            return hiY; // p is below every anchor → clamp to the lowest
        }

        if (!haveHi)
        {
            return loY; // p is above every anchor → clamp to the highest
        }

        var dx = hiX - loX;
        if (dx <= 1e-6f)
        {
            return loY;
        }

        var t = (p - loX) / dx;
        t = t * t * (3f - 2f * t); // smoothstep
        return loY + (hiY - loY) * t;
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

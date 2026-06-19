using System.Numerics;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.SpeedTree;

public sealed class SptBezierSplineTests
{
    [Fact]
    public void Curve_NormalizesControlPointTangentsBeforeApplyingHandleLength()
    {
        var spline = new SptBezierSpline
        {
            Header = new Vector3(0, 1, 0),
            ControlPoints =
            [
                new SptSplineControlPoint(0f, 0f, 0f, 2f, 0.5f),
                new SptSplineControlPoint(1f, 0f, 0f, -2f, 0.5f)
            ]
        };

        Assert.Equal(ExpectedCurve(spline.ControlPoints, 0.5f, 500, true), spline.Curve(0.5f), 6);
        Assert.NotEqual(ExpectedCurve(spline.ControlPoints, 0.5f, 500, false), spline.Curve(0.5f), 4);
    }

    [Fact]
    public void Curve_UsesFiveHundredSampleLookup()
    {
        var spline = new SptBezierSpline
        {
            Header = new Vector3(0, 1, 0),
            ControlPoints =
            [
                new SptSplineControlPoint(0f, 0f, 0.2f, 1.4f, 0.25f),
                new SptSplineControlPoint(0.37f, 0.91f, 1.2f, -0.4f, 0.33f),
                new SptSplineControlPoint(1f, 0.2f, 0.7f, -0.9f, 0.21f)
            ]
        };

        var expected500 = ExpectedCurve(spline.ControlPoints, 0.43f, 500, true);
        var expected64 = ExpectedCurve(spline.ControlPoints, 0.43f, 64, true);

        Assert.Equal(expected500, spline.Curve(0.43f), 6);
        Assert.True(MathF.Abs(expected500 - expected64) > 0.00001f);
    }

    [Fact]
    public void Evaluate_MinEqualsMaxStillAppliesVariance()
    {
        var spline = new SptBezierSpline { Header = new Vector3(5f, 5f, 2f) };

        Assert.Equal(7f, spline.Evaluate(0.75f, (_, max) => max));
    }

    private static float ExpectedCurve(
        IReadOnlyList<SptSplineControlPoint> cps, float param, int samples, bool normalizeTangents)
    {
        var p = Math.Clamp(param, 0f, 1f);
        var sx = new float[samples];
        var sy = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            var (x, y) = RawPoint(cps, i / (float)(samples - 1), normalizeTangents);
            sx[i] = x;
            sy[i] = y;
        }

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

        return sy[^1];
    }

    private static (float X, float Y) RawPoint(
        IReadOnlyList<SptSplineControlPoint> cps, float t, bool normalizeTangents)
    {
        var n = cps.Count;
        var ft = t * (n - 1);
        var k = Math.Clamp((int)ft, 0, n - 2);
        var lt = ft - k;
        var c0 = cps[k];
        var c1 = cps[k + 1];
        var p0 = new Vector2(c0.Param, c0.A);
        var p3 = new Vector2(c1.Param, c1.A);
        var v0 = new Vector2(c0.B, c0.C);
        var v1 = new Vector2(c1.B, c1.C);
        if (normalizeTangents)
        {
            v0 = NormalizeOrZero(v0);
            v1 = NormalizeOrZero(v1);
        }

        var p1 = p0 + v0 * c0.D;
        var p2 = p3 - v1 * c1.D;
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
}
using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Modeling.Xngine;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     The reference UV rule (cut-1c plan section 3.2, "Portable UVs") with hand-computed expectations: accumulation on
///     a triangle, the plane fit on an n-gon (a square mapped at 16 units per native unit), the raw fallback of a
///     singular fit, and the packed-UV unfold only when asked. The fit is also checked value for value against the legacy
///     decomposer's on random polygons, since the A3x oracle requires the two to agree exactly.
/// </summary>
public sealed class XnGineUvRuleTests
{
    /// <summary>Corners 1 and 2 of a triangle are deltas: (10, 20), (+5, +6), (+7, +8) accumulate to (15, 26), (22, 34).</summary>
    [Fact]
    public void Triangle_AccumulatesCornersOneAndTwo()
    {
        Vector3[] corners = [new(0, 0, 0), new(256, 0, 0), new(0, 0, 256)];

        var result = XnGineUvRule.Compute(corners, [new(10, 20), new(5, 6), new(7, 8)], unfold: false);

        Assert.Equal(new[] { new XnGineCornerUv(10, 20), new XnGineCornerUv(15, 26), new XnGineCornerUv(22, 34) },
            result.Values);
        Assert.False(result.DegenerateFit);
        Assert.Equal(0, result.UnfoldedValues);
    }

    /// <summary>
    ///     A pentagon in the XZ plane with u = 16 x and v = 16 z through its first three corners: the fit reproduces the
    ///     map at the later corners (128, 256) -> (2048, 4096) and (0, 256) -> (0, 4096) and ignores their stored values.
    /// </summary>
    [Fact]
    public void NGon_TakesLaterCornersFromThePlaneFit()
    {
        Vector3[] corners = [new(0, 16, 0), new(256, 16, 0), new(256, 16, 256), new(128, 16, 256), new(0, 16, 256)];
        XnGineCornerUv[] stored = [new(0, 0), new(4096, 0), new(0, 4096), new(7, 9), new(0, 4096)];

        var result = XnGineUvRule.Compute(corners, stored, unfold: false);

        Assert.Equal(new[]
        {
            new XnGineCornerUv(0, 0), new XnGineCornerUv(4096, 0), new XnGineCornerUv(4096, 4096),
            new XnGineCornerUv(2048, 4096), new XnGineCornerUv(0, 4096)
        }, result.Values);
        Assert.False(result.DegenerateFit);
    }

    /// <summary>
    ///     A singular fit (the first three corners collinear) leaves EVERY corner at its raw value, corners 1 and 2
    ///     included, which are then not accumulated. Control: the same values on a non-degenerate pentagon are.
    /// </summary>
    [Fact]
    public void SingularFit_KeepsEveryRawValue()
    {
        Vector3[] collinear = [new(0, 0, 0), new(128, 0, 0), new(256, 0, 0), new(256, 0, 256), new(0, 0, 256)];
        XnGineCornerUv[] stored = [new(1, 2), new(3, 4), new(5, 6), new(7, 8), new(9, 10)];

        var result = XnGineUvRule.Compute(collinear, stored, unfold: false);

        Assert.True(result.DegenerateFit);
        Assert.Equal(stored, result.Values);

        Vector3[] square = [new(0, 0, 0), new(256, 0, 0), new(256, 0, 256), new(128, 0, 256), new(0, 0, 256)];
        var control = XnGineUvRule.Compute(square, stored, unfold: false);
        Assert.False(control.DegenerateFit);
        Assert.Equal(new XnGineCornerUv(4, 6), control.Values[1]);
    }

    /// <summary>
    ///     The unfold (plan D2) touches corners 0 to 2 only when asked: 16000 folds to -384 (the nearest multiple of 8192
    ///     is 16384). That it never reaches corner 3 or later is pinned by
    ///     <see cref="Unfold_NeverTouchesCornerThreeOrLater_OnADegenerateFit" />, since a triangle has no corner 3.
    /// </summary>
    [Fact]
    public void Unfold_AppliesToCornersZeroToTwo_OnlyWhenAsked()
    {
        Vector3[] corners = [new(0, 0, 0), new(256, 0, 0), new(0, 0, 256)];
        XnGineCornerUv[] stored = [new(16000, 0), new(0, 0), new(0, 0)];

        var unfolded = XnGineUvRule.Compute(corners, stored, unfold: true);
        var raw = XnGineUvRule.Compute(corners, stored, unfold: false);

        Assert.Equal(-384, XnGineMesh.UnpackUv(16000));
        Assert.Equal(new XnGineCornerUv(-384, 0), unfolded.Values[0]);
        Assert.Equal(1, unfolded.UnfoldedValues);
        Assert.Equal(new XnGineCornerUv(16000, 0), raw.Values[0]);
        Assert.Equal(0, raw.UnfoldedValues);
    }

    /// <summary>
    ///     The unfold never reaches corner 3 or later (plan D2), shown where later raw values reach the output: a
    ///     degenerate-fit n-gon (its first three corners collinear) keeps every corner's raw value, so corner 0's 16000
    ///     folds to -384 while corner 3's 16000 and corner 4's -16000 stay, and <c>UnfoldedValues</c> counts corner 0
    ///     alone. Control: unfolding every corner would give -384 and 384 there, which the pin rejects.
    /// </summary>
    [Fact]
    public void Unfold_NeverTouchesCornerThreeOrLater_OnADegenerateFit()
    {
        Vector3[] collinear = [new(0, 0, 0), new(128, 0, 0), new(256, 0, 0), new(256, 0, 256), new(0, 0, 256)];
        XnGineCornerUv[] stored = [new(16000, 1), new(2, 3), new(4, 5), new(16000, 6), new(-16000, 7)];

        var result = XnGineUvRule.Compute(collinear, stored, unfold: true);

        Assert.True(result.DegenerateFit);
        Assert.Equal(new[]
        {
            new XnGineCornerUv(-384, 1), new XnGineCornerUv(2, 3), new XnGineCornerUv(4, 5),
            new XnGineCornerUv(16000, 6), new XnGineCornerUv(-16000, 7)
        }, result.Values);
        Assert.Equal(1, result.UnfoldedValues);

        // Control: an unfold over every corner changes corners 3 and 4 as well.
        var everyCorner = stored.Select(c => new XnGineCornerUv(XnGineMesh.UnpackUv(c.U), XnGineMesh.UnpackUv(c.V)))
            .ToArray();
        Assert.Equal(new XnGineCornerUv(-384, 6), everyCorner[3]);
        Assert.Equal(new XnGineCornerUv(384, 7), everyCorner[4]);
        Assert.NotEqual(everyCorner, result.Values);
    }

    /// <summary>
    ///     The n-gon fit agrees with the legacy decomposer's (<c>XnGineMeshDecomposer.ComputeFaceUv</c>) on random
    ///     polygons value for value, singular fits included, because the A3x oracle compares the two exactly.
    /// </summary>
    [Fact]
    public void Fit_AgreesWithTheLegacyDecomposer_OnRandomPolygons()
    {
        var random = new Random(9050);
        var singular = 0;
        for (var trial = 0; trial < 2000; trial++)
        {
            var count = random.Next(4, 10);
            var positions = new XnGineMeshPoint[count];
            var stored = new XnGineCornerUv[count];
            for (var q = 0; q < count; q++)
            {
                positions[q] = trial % 50 == 0 && q < 3
                    ? new XnGineMeshPoint(q * 256, 0, 0)
                    : new XnGineMeshPoint(random.Next(-8192, 8192), random.Next(-8192, 8192), random.Next(-8192, 8192));
                stored[q] = new XnGineCornerUv(random.Next(-8192, 8192), random.Next(-8192, 8192));
            }

            var pure = new XnGineMeshDecomposer.PurePoint[count];
            for (var q = 0; q < count; q++)
            {
                pure[q] = new XnGineMeshDecomposer.PurePoint(positions[q], new XnGineMeshPoint(0, 0, 0), stored[q].U,
                    stored[q].V);
            }

            var legacy = XnGineMeshDecomposer.ComputeFaceUv(pure);
            var result = XnGineUvRule.Compute(XnGineTriangulation.CornerVectors(positions), stored, unfold: false);

            Assert.Equal(legacy is null, result.DegenerateFit);
            var expected = legacy?.Select(p => new XnGineCornerUv(p.U, p.V)).ToArray() ?? stored;
            Assert.Equal(expected, result.Values);
            singular += result.DegenerateFit ? 1 : 0;
        }

        Assert.True(singular > 0, "the random set must exercise the singular fallback");
    }
}

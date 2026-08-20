using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class ExportTileContentCoverageTests
{
    private static readonly IReadOnlyList<NifWaterGeometry> NoWater = Array.Empty<NifWaterGeometry>();

    [Fact]
    public void MayContainContent_AdmitsCellAtWorkingSquareCornerOutsideEuclideanCircle()
    {
        var cylinder = new VisibilityCylinder(Vector3.Zero, 10f);

        var result = ExportTileContentCoverage.MayContainContent(
            cylinder, 4f, [(15.5f, 15.5f)], NoWater);

        Assert.True(result);
        Assert.True(MathF.Sqrt(15.5f * 15.5f + 15.5f * 15.5f) > cylinder.Radius);
    }

    [Fact]
    public void MayContainContent_AdmitsCellTouchingExitMarginBoundary()
    {
        // 10 tile half-extent + 4 renderer exit margin + 2 cell half-extent = center X 16.
        Assert.True(ExportTileContentCoverage.MayContainContent(
            new VisibilityCylinder(Vector3.Zero, 10f),
            4f,
            [(16f, 0f)],
            NoWater));
    }

    [Fact]
    public void MayContainContent_RejectsCellWhoseFootprintIsTrulyOutsideWorkingSquare()
    {
        Assert.False(ExportTileContentCoverage.MayContainContent(
            new VisibilityCylinder(Vector3.Zero, 10f),
            4f,
            [(16.01f, 0f)],
            NoWater));
    }

    [Fact]
    public void MayContainContent_InvalidInputsFailOpen()
    {
        var cylinder = new VisibilityCylinder(Vector3.Zero, 10f);

        Assert.True(ExportTileContentCoverage.MayContainContent(
            new VisibilityCylinder(new Vector3(float.NaN, 0f, 0f), 10f), 4f, [], NoWater));
        Assert.True(ExportTileContentCoverage.MayContainContent(
            new VisibilityCylinder(Vector3.Zero, float.NaN), 4f, [], NoWater));
        Assert.True(ExportTileContentCoverage.MayContainContent(
            new VisibilityCylinder(Vector3.Zero, -1f), 4f, [], NoWater));
        Assert.True(ExportTileContentCoverage.MayContainContent(
            new VisibilityCylinder(Vector3.Zero, float.MaxValue), 4f, [], NoWater));
        Assert.True(ExportTileContentCoverage.MayContainContent(
            cylinder, 0f, [], NoWater));
        Assert.True(ExportTileContentCoverage.MayContainContent(
            cylinder, 4f, [(float.NaN, 0f)], NoWater));
        Assert.True(ExportTileContentCoverage.MayContainContent(
            cylinder, 4f, null!, NoWater));
        Assert.True(ExportTileContentCoverage.MayContainContent(
            cylinder, 4f, [], null!));
    }

    [Fact]
    public void MayContainContent_AdmitsAccumulatedNifWaterCrossingTileFromRemoteHomeCell()
    {
        Vector3[] positions =
        [
            new(13.5f, -1f, 2f),
            new(15f, -1f, 2f),
            new(15f, 1f, 2f)
        ];
        Assert.True(NifWaterGeometry.TryCreate(positions, [0, 1, 2], out var water));

        var result = ExportTileContentCoverage.MayContainContent(
            new VisibilityCylinder(Vector3.Zero, 10f),
            4f,
            [(100f, 100f)],
            [water!]);

        Assert.True(result);
    }
}
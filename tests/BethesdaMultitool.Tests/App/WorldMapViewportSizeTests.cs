using System.Numerics;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldMapViewportSizeTests
{
    [Theory]
    [InlineData(657f, 583f)]
    [InlineData(1200f, 800f)]
    public void FittedFiveCellWorld_TransfersItsActualCenter(float width, float height)
    {
        // Replay006: five exterior cells in one row, fitted with the measured canvas and 10% margin.
        var zoom = Math.Min(width / 20480f, height / 4096f) * 0.9f;
        var fittedPan = new Vector2(width * 0.05f, height * 0.5f + 2048f * zoom);
        var size = WorldMapViewportMath.ResolveCanvasSize(width, height, new Vector2(800f, 600f));

        var center = WorldMapViewportMath.GetCenterWorld(size, zoom, fittedPan);

        Assert.Equal(new Vector2(width, height), size);
        Assert.Equal(10240f, center.X, 0.01f);
        Assert.Equal(2048f, -center.Y, 0.01f); // map Y → shared 3D Y
    }

    [Theory]
    [InlineData(657f, 583f)]
    [InlineData(1200f, 800f)]
    public void CenteringAWorldPoint_UsesTheMeasuredCanvas(float width, float height)
    {
        var size = WorldMapViewportMath.ResolveCanvasSize(width, height, new Vector2(800f, 600f));
        const float zoom = 0.05f;
        var target = new Vector2(10240f, -2048f);

        var pan = WorldMapViewportMath.CenterOnWorld(size, zoom, target);

        // Check the physical screen centre, independently of the capture helper.
        Assert.Equal(width * 0.5f, target.X * zoom + pan.X, 0.001f);
        Assert.Equal(height * 0.5f, target.Y * zoom + pan.Y, 0.001f);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(657f, 0f)]
    [InlineData(float.NaN, 583f)]
    public void UnavailableLayout_UsesLastCompleteCanvas(float width, float height)
    {
        var retainedSize = new Vector2(657f, 583f);
        var size = WorldMapViewportMath.ResolveCanvasSize(width, height, retainedSize);
        var target = new Vector2(10240f, -2048f);
        var pan = WorldMapViewportMath.CenterOnWorld(size, 0.05f, target);

        Assert.Equal(retainedSize, size);
        var center = WorldMapViewportMath.GetCenterWorld(retainedSize, 0.05f, pan);
        Assert.Equal(target.X, center.X, 0.01f);
        Assert.Equal(target.Y, center.Y, 0.01f);
    }

    [Theory]
    [InlineData(0f, 0f, 657f, 583f)] // navigate before first layout
    [InlineData(657f, 583f, 1200f, 800f)] // resize while the map is collapsed
    public void IncomingFocus_RemainsCenteredWhenTheMapBecomesVisible(
        float lastWidth, float lastHeight, float width, float height)
    {
        var referenceSize = WorldMapViewportMath.ResolveCanvasSize(0f, 0f, new Vector2(lastWidth, lastHeight));
        if (lastWidth == 0f) Assert.Equal(new Vector2(800f, 600f), referenceSize);
        var target = new Vector2(10240f, -2048f);
        const float zoom = 0.05f;
        var pan = WorldMapViewportMath.CenterOnWorld(referenceSize, zoom, target);

        var visiblePan = WorldMapViewportMath.PreserveCenterOnResize(
            pan, zoom, referenceSize.X, referenceSize.Y, width, height);

        Assert.Equal(target.X, (width * 0.5f - visiblePan.X) / zoom, 0.01f);
        Assert.Equal(target.Y, (height * 0.5f - visiblePan.Y) / zoom, 0.01f);
    }
}

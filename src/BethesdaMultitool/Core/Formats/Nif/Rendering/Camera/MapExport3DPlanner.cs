using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Pure geometry for the 3D export: from the active worldspace's world-space AABB + a projection
///     mode/direction + a scale, computes the framing (focus, azimuth/elevation, ortho extent, aspect)
///     and the tile grid. The render side feeds each tile through
///     <see cref="OrthoViewProjBuilder.BuildViewProjTile" />, which subdivides the SAME global ortho clip
///     volume so the tiles stitch seamlessly at any elevation.
///     <para>
///         Scale is anchored on the player model: <c>1×</c> renders a player-height object at 64 px tall,
///         i.e. <see cref="BaseScalePxPerUnit" /> world-pixels per unit. The final image keeps
///         <c>ImageWidth / ImageHeight == Aspect</c> so pixels stay square regardless of tile rounding.
///     </para>
/// </summary>
internal static class MapExport3DPlanner
{
    /// <summary>FNV player capsule height in world units (the camera controller's documented 128).
    /// The visible body spans roughly this, so it's the reference height for the px/unit scale.</summary>
    public const float PlayerModelHeightUnits = 128f;

    /// <summary>World-pixels per unit at <c>1×</c>: a <see cref="PlayerModelHeightUnits" />-tall object
    /// renders 64 px tall (64 / 128 = 0.5 px/unit).</summary>
    public const float BaseScalePxPerUnit = 64f / PlayerModelHeightUnits;

    /// <summary>A hair of margin on the projected extent so independent W/H rounding never crops.</summary>
    private const float ExtentMargin = 1.01f;

    public static Export3DPlan Plan(
        ProjectionMode mode, int directionQuadrant,
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        float worldMinZ, float worldMaxZ,
        float scale, bool tiled, int maxTileDim, int maxImageDim)
    {
        var azimuth = OrthoViewProjBuilder.AzimuthDegFor(mode, directionQuadrant);
        var elevation = OrthoViewProjBuilder.ElevationDegFor(mode);
        var focus = new Vector3(
            (worldMinX + worldMaxX) * 0.5f,
            (worldMinY + worldMaxY) * 0.5f,
            (worldMinZ + worldMaxZ) * 0.5f);

        // View-space half-extents = the max |projection| of the 8 AABB corners onto the camera right/up
        // axes. For top-down both axes are horizontal so the Z span is irrelevant; for iso/tri the up
        // axis tilts, so the Z span sets how much vertical relief the frame must cover.
        var (right, up) = OrthoViewProjBuilder.CameraBasis(azimuth, elevation);
        float halfW = 1f, halfH = 1f;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? worldMinX : worldMaxX,
                (i & 2) == 0 ? worldMinY : worldMaxY,
                (i & 4) == 0 ? worldMinZ : worldMaxZ);
            var d = corner - focus;
            halfW = MathF.Max(halfW, MathF.Abs(Vector3.Dot(d, right)));
            halfH = MathF.Max(halfH, MathF.Abs(Vector3.Dot(d, up)));
        }
        halfW *= ExtentMargin;
        halfH *= ExtentMargin;

        // Keep the true projection aspect so the image is never squashed: anchor on the requested
        // height, derive the width from the aspect.
        var aspect0 = halfW / halfH;
        var pxPerUnit = BaseScalePxPerUnit * MathF.Max(scale, 1e-4f);
        var wantH = Math.Max(1, (int)MathF.Ceiling(2f * halfH * pxPerUnit));
        var wantW = Math.Max(1, (int)MathF.Round(wantH * aspect0));

        // Two DISTINCT caps: maxTileDim bounds the per-tile GPU render target (an MSAA offscreen
        // memory ceiling — it is NOT an output-size limit), while maxImageDim bounds the output image
        // of a non-tiled export. A non-tiled export larger than one tile is rendered internally tiled
        // and stitched to a single PNG by the caller; conflating the two caps used to collapse every
        // real non-tiled export to a maxTileDim-long-edge thumbnail. A tiled export writes per-tile
        // PNGs + a manifest and has no overall image cap.
        var cap = Math.Max(1, maxTileDim);
        if (!tiled)
        {
            // Single image: cap the long edge, scaling BOTH axes so the aspect is preserved (capping
            // each axis independently would square a wide worldspace). Never below one tile. Tile
            // rounding below can overshoot the cap by up to cols−1 px — harmless for a CPU-side image.
            var imageCap = Math.Max(cap, maxImageDim);
            if (wantW > imageCap || wantH > imageCap)
            {
                var s = imageCap / (float)Math.Max(wantW, wantH);
                wantW = Math.Max(1, (int)MathF.Round(wantW * s));
                wantH = Math.Max(1, (int)MathF.Round(wantH * s));
            }
        }
        var cols = Math.Max(1, (wantW + cap - 1) / cap);
        var rows = Math.Max(1, (wantH + cap - 1) / cap);

        // Uniform tile pixel size (the off-center frustum splits the clip into equal cells), capped per
        // tile; the final image is exactly cols·tileW × rows·tileH.
        var tileW = Math.Clamp((wantW + cols - 1) / cols, 1, cap);
        var tileH = Math.Clamp((wantH + rows - 1) / rows, 1, cap);
        var imageW = cols * tileW;
        var imageH = rows * tileH;

        // Match the projection aspect to the actual pixel grid so pixels are square; halfH stays the
        // vertical anchor and halfW follows the grid aspect.
        var aspect = imageW / (float)imageH;
        return new Export3DPlan(focus, azimuth, elevation, halfH, aspect, cols, rows, tileW, tileH, imageW, imageH);
    }
}

/// <summary>
///     A planned 3D export: the global ortho framing (<see cref="Focus" />, <see cref="Azimuth" />,
///     <see cref="Elevation" />, <see cref="OrthoHalfHeight" />, <see cref="Aspect" />) plus the tile grid
///     (<see cref="Cols" />×<see cref="Rows" /> tiles of <see cref="TileWidth" />×<see cref="TileHeight" />
///     px, composing a <see cref="ImageWidth" />×<see cref="ImageHeight" /> image).
/// </summary>
internal sealed record Export3DPlan(
    Vector3 Focus,
    float Azimuth,
    float Elevation,
    float OrthoHalfHeight,
    float Aspect,
    int Cols,
    int Rows,
    int TileWidth,
    int TileHeight,
    int ImageWidth,
    int ImageHeight);

using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Composes rendered export tiles into one RGBA image buffer. The non-tiled 3D export renders
///     internally tiled when the image exceeds the per-tile GPU target cap and stitches the tiles into
///     a single PNG; because the tiles are off-center sub-frustums of ONE global ortho clip volume
///     (<see cref="OrthoViewProjBuilder.BuildViewProjTile" />), composition is pure row copying with no
///     seam correction at any elevation.
/// </summary>
internal static class ExportTileStitcher
{
    /// <summary>
    ///     Copies a tightly-packed RGBA tile into its (<paramref name="tileCol" />, <paramref name="tileRow" />)
    ///     grid slot of the destination image buffer. The grid is uniform (image = cols·tileWidth ×
    ///     rows·tileHeight), so the slot is fully inside the image or the call throws.
    /// </summary>
    public static void CopyTile(
        byte[] tileRgba, int tileWidth, int tileHeight,
        byte[] imageRgba, int imageWidth, int imageHeight,
        int tileCol, int tileRow)
    {
        if (tileWidth <= 0 || tileHeight <= 0) throw new ArgumentOutOfRangeException(nameof(tileWidth));
        ArgumentOutOfRangeException.ThrowIfNegative(tileCol);
        ArgumentOutOfRangeException.ThrowIfNegative(tileRow);
        if (tileRgba.Length < tileWidth * tileHeight * 4)
        {
            throw new ArgumentException("Tile buffer smaller than tileWidth×tileHeight×4.", nameof(tileRgba));
        }
        var x0 = (long)tileCol * tileWidth;
        var y0 = (long)tileRow * tileHeight;
        if (x0 + tileWidth > imageWidth || y0 + tileHeight > imageHeight)
        {
            throw new ArgumentException(
                $"Tile ({tileCol},{tileRow}) of {tileWidth}×{tileHeight} exceeds the {imageWidth}×{imageHeight} image.");
        }
        if (imageRgba.Length < (long)imageWidth * imageHeight * 4)
        {
            throw new ArgumentException("Image buffer smaller than imageWidth×imageHeight×4.", nameof(imageRgba));
        }

        var rowBytes = tileWidth * 4;
        for (var y = 0; y < tileHeight; y++)
        {
            var src = y * rowBytes;
            var dst = ((((y0 + y) * imageWidth) + x0) * 4);
            Buffer.BlockCopy(tileRgba, src, imageRgba, checked((int)dst), rowBytes);
        }
    }
}

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     What a decoded picture asset actually is: the full stored size of its first frame and how many
///     frames it carries. A gallery cell shows a scaled thumbnail; this is the size a caption should
///     quote, because the thumbnail's own dimensions describe the cell, not the asset.
/// </summary>
/// <param name="Width">The first frame's stored width in pixels, before any thumbnail scaling.</param>
/// <param name="Height">The first frame's stored height in pixels, before any thumbnail scaling.</param>
/// <param name="FrameCount">The number of decoded frames; one for a texture, the distinct frame count for a sprite.</param>
public sealed record AssetImageInfo(int Width, int Height, int FrameCount)
{
    /// <summary>A single-frame picture, the shape every texture and most classic screens take.</summary>
    public static AssetImageInfo Single(int width, int height) => new(width, height, 1);
}

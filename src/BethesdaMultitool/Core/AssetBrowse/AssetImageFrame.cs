namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>One frame of a decoded picture asset at its stored size.</summary>
/// <param name="Label">The frame's label: the file name for a texture, the decoder's per-frame label for a sprite.</param>
/// <param name="Width">The stored width in pixels.</param>
/// <param name="Height">The stored height in pixels.</param>
/// <param name="Rgba">Straight (not premultiplied) RGBA, <c>Width * Height * 4</c> bytes; shared with the decoder, never copied.</param>
internal sealed record AssetImageFrame(string Label, int Width, int Height, byte[] Rgba);

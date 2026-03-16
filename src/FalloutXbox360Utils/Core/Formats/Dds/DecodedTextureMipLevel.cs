namespace FalloutXbox360Utils.Core.Formats.Dds;

/// <summary>
///     One RGBA mip level ready for sampling.
/// </summary>
internal sealed class DecodedTextureMipLevel
{
    /// <summary>RGBA pixel data (length = Width * Height * 4).</summary>
    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
}
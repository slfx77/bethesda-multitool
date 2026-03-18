namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     A single entry in a Bethesda Texture Atlas Index (.tai) file.
///     UV coordinates are normalized [0,1] relative to the atlas dimensions.
/// </summary>
public readonly record struct TaiEntry(
    string VirtualPath,
    string AtlasName,
    int AtlasIndex,
    float UOffset,
    float VOffset,
    float UWidth,
    float VHeight)
{
    /// <summary>The filename portion of the virtual path (without extension).</summary>
    public string Name => Path.GetFileNameWithoutExtension(VirtualPath);

    /// <summary>Convert UV coordinates to pixel rectangle given atlas dimensions.</summary>
    public (int X, int Y, int Width, int Height) ToPixelRect(int atlasWidth, int atlasHeight)
    {
        return ((int)(UOffset * atlasWidth),
            (int)(VOffset * atlasHeight),
            (int)(UWidth * atlasWidth),
            (int)(VHeight * atlasHeight));
    }
}

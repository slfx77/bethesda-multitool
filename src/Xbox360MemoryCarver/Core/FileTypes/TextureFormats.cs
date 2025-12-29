using System.Collections.Frozen;

namespace Xbox360MemoryCarver.Core.FileTypes;

/// <summary>
///     Constants for Xbox 360 texture formats.
/// </summary>
public static class TextureFormats
{
    /// <summary>
    ///     Xbox 360 GPU texture formats (from DDXConv/Xenia).
    /// </summary>
    public static FrozenDictionary<int, string> Xbox360GpuTextureFormats { get; } = new Dictionary<int, string>
    {
        [0x12] = "DXT1",
        [0x13] = "DXT3",
        [0x14] = "DXT5",
        [0x52] = "DXT1",
        [0x53] = "DXT3",
        [0x54] = "DXT5",
        [0x71] = "ATI2",
        [0x7B] = "ATI1",
        [0x82] = "DXT1",
        [0x86] = "DXT1",
        [0x88] = "DXT5"
    }.ToFrozenDictionary();

    /// <summary>
    ///     Bytes per compression block for each format.
    /// </summary>
    public static FrozenDictionary<string, int> BytesPerBlock { get; } = new Dictionary<string, int>
    {
        ["DXT1"] = 8,
        ["DXT3"] = 16,
        ["DXT5"] = 16,
        ["ATI1"] = 8,
        ["ATI2"] = 16,
        ["BC4U"] = 8,
        ["BC4S"] = 8,
        ["BC5U"] = 16,
        ["BC5S"] = 16
    }.ToFrozenDictionary();

    /// <summary>
    ///     Get bytes per block for a format, defaulting to 16 if unknown.
    /// </summary>
    public static int GetBytesPerBlock(string fourcc)
    {
        return BytesPerBlock.TryGetValue(fourcc, out var bytes) ? bytes : 16;
    }
}

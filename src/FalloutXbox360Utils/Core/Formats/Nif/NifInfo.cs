namespace FalloutXbox360Utils.Core.Formats.Nif;

/// <summary>
///     Information about a NIF file.
/// </summary>
public sealed class NifInfo
{
    public string HeaderString { get; set; } = "";
    public uint BinaryVersion { get; set; }
    public bool IsBigEndian { get; set; }
    public uint UserVersion { get; set; }
    public uint BsVersion { get; set; }
    public int BlockCount { get; set; }
    public List<BlockInfo> Blocks { get; } = [];
    public List<string> BlockTypeNames { get; } = [];
    public List<string> Strings { get; } = [];

    /// <summary>
    ///     Get the type name for a block by index.
    /// </summary>
    public string GetBlockTypeName(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= Blocks.Count)
        {
            return "Invalid";
        }

        return Blocks[blockIndex].TypeName;
    }
}

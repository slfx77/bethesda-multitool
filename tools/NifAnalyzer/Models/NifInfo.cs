namespace NifAnalyzer.Models;

/// <summary>
///     Contains parsed NIF file header information.
/// </summary>
internal class NifInfo
{
    public string VersionString { get; set; } = "";
    public uint Version { get; set; }
    public bool IsBigEndian { get; set; }
    public uint UserVersion { get; set; }
    public int NumBlocks { get; set; }
    public int BsVersion { get; set; }
    public List<string> BlockTypes { get; set; } = [];
    public ushort[] BlockTypeIndices { get; set; } = [];
    public uint[] BlockSizes { get; set; } = [];
    public int NumStrings { get; set; }
    public List<string> Strings { get; set; } = [];
    public int BlockDataOffset { get; set; }

    /// <summary>
    ///     Calculates the file offset for a specific block index.
    /// </summary>
    public int GetBlockOffset(int blockIndex)
    {
        var offset = BlockDataOffset;
        for (var i = 0; i < blockIndex; i++)
            offset += (int)BlockSizes[i];
        return offset;
    }

    /// <summary>
    ///     Gets the type name for a specific block index.
    /// </summary>
    public string GetBlockTypeName(int blockIndex)
    {
        return BlockTypes[BlockTypeIndices[blockIndex]];
    }
}
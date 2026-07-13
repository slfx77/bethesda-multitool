namespace NifAnalyzer.Models;

/// <summary>
///     NIF header + block-table view used by NifAnalyzer's commands. This is a thin PROJECTION of the
///     main application's authoritative parse (<see cref="NifAnalyzer.Parsers.NifParser" /> delegates
///     to <c>BethesdaMultitool.Core.Formats.Nif.Parser.NifParser</c>) — the tool must decode NIFs with
///     the same code the app renders them with, so a debug session can never chase a bug that only
///     exists in a divergent fork. Per-block byte offsets come straight from the app parser's measure
///     walk, so they are correct even for formats whose blocks are not contiguous by size alone
///     (Morrowind 4.0.0.2 separates blocks with inline type-name prefixes; Oblivion legacy streams
///     carry a per-block word).
/// </summary>
internal sealed class NifInfo
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
    public int MaxStringLength { get; set; }
    public List<string> Strings { get; set; } = [];
    public int BlockDataOffset { get; set; }

    /// <summary>
    ///     Authoritative per-block file offsets from the app parser's measure walk. Empty only when the
    ///     parser could not size the block list; <see cref="GetBlockOffset" /> then falls back to a
    ///     contiguous-size accumulation (valid for modern block-size-array NIFs).
    /// </summary>
    public int[] BlockOffsets { get; set; } = [];

    /// <summary>
    ///     File offset for a block index — the app parser's exact <c>BlockInfo.DataOffset</c> when
    ///     available (the value the render/decode path itself keys off), otherwise a size accumulation.
    /// </summary>
    public int GetBlockOffset(int blockIndex)
    {
        if (blockIndex >= 0 && blockIndex < BlockOffsets.Length)
        {
            return BlockOffsets[blockIndex];
        }

        var offset = BlockDataOffset;
        for (var i = 0; i < blockIndex; i++)
            offset += (int)BlockSizes[i];
        return offset;
    }

    /// <summary>
    ///     Type name for a block index. Resolves through the (rebuilt) dedup table, which is populated
    ///     for every NIF version — including Morrowind, which has no on-disk block-types table.
    /// </summary>
    public string GetBlockTypeName(int blockIndex)
    {
        return BlockTypes[BlockTypeIndices[blockIndex]];
    }
}

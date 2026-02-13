using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Schema;
using FalloutXbox360Utils.Core.Formats.Nif.Skinning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Holds all mutable conversion state for a single NIF conversion pass.
///     Created fresh per conversion, populated by NifDiscovery, read by NifOutputWriter.
/// </summary>
internal sealed class NifConversionState
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>Blocks to strip from output (BSPackedAdditionalGeometryData).</summary>
    public readonly HashSet<int> BlocksToStrip = [];

    /// <summary>Geometry blocks that need expansion, keyed by geometry block index.</summary>
    public readonly Dictionary<int, GeometryBlockExpansion> GeometryExpansions = [];

    /// <summary>Triangles extracted from NiTriStripsData strips (for non-skinned meshes), keyed by geometry block index.</summary>
    public readonly Dictionary<int, ushort[]> GeometryStripTriangles = [];

    /// <summary>Maps geometry block index to its associated NiSkinPartition block index.</summary>
    public readonly Dictionary<int, int> GeometryToSkinPartition = [];

    /// <summary>Havok collision blocks that need HalfVector3 -> Vector3 expansion.</summary>
    public readonly Dictionary<int, HavokBlockExpansion> HavokExpansions = [];

    /// <summary>New strings to add to the string table (for node names).</summary>
    public readonly List<string> NewStrings = [];

    /// <summary>Block index -> node name mapping from NiDefaultAVObjectPalette.</summary>
    public readonly Dictionary<int, string> NodeNamesByBlock = [];

    /// <summary>Maps block index -> string table index (for NiNode Name field restoration).</summary>
    public readonly Dictionary<int, int> NodeNameStringIndices = [];

    /// <summary>Extracted geometry data indexed by packed block index.</summary>
    public readonly Dictionary<int, PackedGeometryData> PackedGeometryByBlock = [];

    /// <summary>NiSkinPartition blocks that need bone weights/indices expansion.</summary>
    public readonly Dictionary<int, SkinPartitionExpansion> SkinPartitionExpansions = [];

    /// <summary>Maps NiSkinPartition block index to its associated packed geometry data.</summary>
    public readonly Dictionary<int, PackedGeometryData> SkinPartitionToPackedData = [];

    /// <summary>Triangles extracted from NiSkinPartition strips, keyed by NiSkinPartition block index.</summary>
    public readonly Dictionary<int, ushort[]> SkinPartitionTriangles = [];

    /// <summary>Vertex maps from NiSkinPartition blocks, keyed by NiSkinPartition block index.</summary>
    public readonly Dictionary<int, ushort[]> VertexMaps = [];

    /// <summary>Original string count (before adding new strings).</summary>
    public int OriginalStringCount;

    /// <summary>NIF schema for schema-driven conversion.</summary>
    public NifSchema Schema { get; }

    public NifConversionState()
    {
        Schema = NifSchema.LoadEmbedded();
    }

    /// <summary>
    ///     Resets all mutable state for a new conversion pass.
    /// </summary>
    public void Reset()
    {
        BlocksToStrip.Clear();
        PackedGeometryByBlock.Clear();
        GeometryExpansions.Clear();
        HavokExpansions.Clear();
        VertexMaps.Clear();
        SkinPartitionTriangles.Clear();
        GeometryStripTriangles.Clear();
        GeometryToSkinPartition.Clear();
        SkinPartitionExpansions.Clear();
        SkinPartitionToPackedData.Clear();
        NodeNamesByBlock.Clear();
        NewStrings.Clear();
        NodeNameStringIndices.Clear();
        OriginalStringCount = 0;
    }

    /// <summary>
    ///     Whether in-place conversion can be used (no expansions, no blocks to strip, no new strings).
    /// </summary>
    public bool CanUseInPlaceConversion =>
        BlocksToStrip.Count == 0 &&
        GeometryExpansions.Count == 0 &&
        HavokExpansions.Count == 0 &&
        SkinPartitionExpansions.Count == 0 &&
        NewStrings.Count == 0;

    /// <summary>
    ///     Gets the output size for a block, accounting for expansions.
    /// </summary>
    public int GetBlockOutputSize(BlockInfo block)
    {
        if (GeometryExpansions.TryGetValue(block.Index, out var expansion))
        {
            return expansion.NewSize;
        }

        if (HavokExpansions.TryGetValue(block.Index, out var havokExpansion))
        {
            return havokExpansion.NewSize;
        }

        if (SkinPartitionExpansions.TryGetValue(block.Index, out var skinPartExpansion))
        {
            return skinPartExpansion.NewSize;
        }

        return block.Size;
    }

    /// <summary>
    ///     Calculate block index remapping after removing packed blocks.
    /// </summary>
    public int[] CalculateBlockRemap(int blockCount)
    {
        var remap = new int[blockCount];
        var newIndex = 0;

        for (var i = 0; i < blockCount; i++)
        {
            remap[i] = BlocksToStrip.Contains(i) ? -1 : newIndex++;
        }

        return remap;
    }

    /// <summary>
    ///     Calculate total output size accounting for removed and expanded blocks.
    /// </summary>
    public int CalculateOutputSize(int originalSize, NifInfo info)
    {
        var size = originalSize;

        Log.Debug($"  Size calculation: starting from {originalSize}");

        foreach (var str in NewStrings)
        {
            size += 4 + str.Length;
            Log.Debug($"    + New string '{str}': {4 + str.Length} bytes");
        }

        foreach (var blockIdx in BlocksToStrip)
        {
            var block = info.Blocks[blockIdx];
            size -= block.Size;
            size -= 4; // Block size entry in header
            size -= 2; // Block type index entry in header
            Log.Debug($"    - Remove block {blockIdx}: {block.Size} + 6 header bytes");
        }

        foreach (var kvp in GeometryExpansions)
        {
            size += kvp.Value.SizeIncrease;
            Log.Debug($"    + Expand geometry block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        foreach (var kvp in HavokExpansions)
        {
            size += kvp.Value.SizeIncrease;
            Log.Debug($"    + Expand Havok block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        foreach (var kvp in SkinPartitionExpansions)
        {
            size += kvp.Value.SizeIncrease;
            Log.Debug($"    + Expand NiSkinPartition block {kvp.Key}: {kvp.Value.SizeIncrease} bytes");
        }

        Log.Debug($"  Final calculated size: {size}");

        return size;
    }
}

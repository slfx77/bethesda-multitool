// NIF converter - Schema-driven big-endian to little-endian conversion
// Uses nif.xml definitions for proper field-by-field byte swapping
// Handles BSPackedAdditionalGeometryData expansion for Xbox 360 NIFs

using System.Buffers.Binary;
using static Xbox360MemoryCarver.Core.Formats.Nif.NifEndianUtils;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Converts Xbox 360 (big-endian) NIF files to PC (little-endian) format.
///     Uses schema-driven conversion based on nif.xml definitions.
///     Handles BSPackedAdditionalGeometryData expansion for geometry blocks.
/// </summary>
internal sealed partial class NifConverter
{
    private static readonly Logger Log = Logger.Instance;

    // Blocks to strip from output (BSPackedAdditionalGeometryData)
    private readonly HashSet<int> _blocksToStrip = [];

    // Geometry blocks that need expansion, keyed by geometry block index
    private readonly Dictionary<int, GeometryBlockExpansion> _geometryExpansions = [];

    // Triangles extracted from NiTriStripsData strips (for non-skinned meshes), keyed by geometry block index
    private readonly Dictionary<int, ushort[]> _geometryStripTriangles = [];

    // Maps geometry block index to its associated NiSkinPartition block index
    private readonly Dictionary<int, int> _geometryToSkinPartition = [];

    // Havok collision blocks that need HalfVector3 -> Vector3 expansion
    private readonly Dictionary<int, HavokBlockExpansion> _havokExpansions = [];

    // New strings to add to the string table (for node names)
    private readonly List<string> _newStrings = [];

    // Block index -> node name mapping from NiDefaultAVObjectPalette
    private readonly Dictionary<int, string> _nodeNamesByBlock = [];

    // Maps block index -> string table index (for NiNode Name field restoration)
    private readonly Dictionary<int, int> _nodeNameStringIndices = [];

    // Extracted geometry data indexed by packed block index
    private readonly Dictionary<int, PackedGeometryData> _packedGeometryByBlock = [];
    private readonly NifSchema _schema;

    // NiSkinPartition blocks that need bone weights/indices expansion
    private readonly Dictionary<int, SkinPartitionExpansion> _skinPartitionExpansions = [];

    // Maps NiSkinPartition block index to its associated packed geometry data
    private readonly Dictionary<int, PackedGeometryData> _skinPartitionToPackedData = [];

    // Triangles extracted from NiSkinPartition strips, keyed by NiSkinPartition block index
    private readonly Dictionary<int, ushort[]> _skinPartitionTriangles = [];

    // Vertex maps from NiSkinPartition blocks, keyed by NiSkinPartition block index
    private readonly Dictionary<int, ushort[]> _vertexMaps = [];

    // Original string count (before adding new strings)
    private int _originalStringCount;

    public NifConverter(bool verbose = false)
    {
        // Configure logger level based on verbose flag
        if (verbose)
        {
            Log.Level = LogLevel.Debug;
        }

        _schema = NifSchema.LoadEmbedded();
    }

    /// <summary>
    ///     Check if a block type is a node type that has a Name field.
    /// </summary>
    private static bool IsNodeType(string typeName)
    {
        return typeName is "NiNode" or "BSFadeNode" or "BSLeafAnimNode" or "BSTreeNode" or
            "BSOrderedNode" or "BSMultiBoundNode" or "BSMasterParticleSystem" or "NiSwitchNode" or
            "NiBillboardNode" or "NiLODNode" or "BSBlastNode" or "BSDamageStage" or "NiAVObject";
    }

    private static void BulkSwap32(byte[] buf, int start, int size)
    {
        // Swap all 4-byte aligned values as a fallback
        var end = Math.Min(start + size, buf.Length - 3);
        for (var i = start; i < end; i += 4)
        {
            SwapUInt32InPlace(buf, i);
        }
    }

    // Big-endian read helpers
    private static ushort ReadUInt16BE(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static int ReadInt32BE(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
    }
}

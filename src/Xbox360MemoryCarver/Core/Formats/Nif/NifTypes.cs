using Xbox360MemoryCarver.Core.Converters;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Result of a NIF conversion operation. Extends the base ConversionResult
///     with NIF-specific metadata about source and output file info.
/// </summary>
internal sealed class NifConversionResult : ConversionResult
{
    /// <summary>
    ///     NIF-specific error message (maps to Notes in base class).
    /// </summary>
    public string? ErrorMessage
    {
        get => Notes;
        init => Notes = value;
    }

    /// <summary>
    ///     Information about the source NIF file.
    /// </summary>
    public NifInfo? SourceInfo { get; init; }

    /// <summary>
    ///     Information about the converted output NIF file.
    /// </summary>
    public NifInfo? OutputInfo { get; init; }
}

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

/// <summary>
///     Information about a single NIF block.
/// </summary>
public sealed class BlockInfo
{
    public int Index { get; set; }
    public ushort TypeIndex { get; set; }
    public string TypeName { get; set; } = "";
    public int Size { get; set; }
    public int DataOffset { get; set; }
}

/// <summary>
///     Geometry data extracted from BSPackedAdditionalGeometryData.
/// </summary>
internal sealed class PackedGeometryData
{
    public ushort NumVertices { get; set; }
    public float[]? Positions { get; set; }
    public float[]? Normals { get; set; }
    public float[]? Tangents { get; set; }
    public float[]? Bitangents { get; set; }
    public float[]? UVs { get; set; }

    /// <summary>Vertex colors as RGBA bytes (4 bytes per vertex).</summary>
    public byte[]? VertexColors { get; set; }

    /// <summary>
    ///     Bone indices for skinned meshes (4 bytes per vertex).
    ///     Each vertex references up to 4 bones by index.
    ///     These are partition-local indices, not global skeleton indices.
    /// </summary>
    public byte[]? BoneIndices { get; set; }

    /// <summary>
    ///     Bone weights for skinned meshes (4 floats per vertex).
    ///     Each weight corresponds to the bone at the same index in BoneIndices.
    ///     Weights should sum to 1.0 for each vertex.
    /// </summary>
    public float[]? BoneWeights { get; set; }

    public ushort BsDataFlags { get; set; }
}

/// <summary>
///     Information about how a geometry block needs to be expanded.
/// </summary>
internal sealed class GeometryBlockExpansion
{
    public int BlockIndex { get; set; }
    public int PackedBlockIndex { get; set; }
    public int SizeIncrease { get; set; }
    public int OriginalSize { get; set; }
    public int NewSize { get; set; }
}

/// <summary>
///     Information about how a Havok block needs vertex decompression.
/// </summary>
internal sealed class HavokBlockExpansion
{
    public int BlockIndex { get; set; }
    public int NumVertices { get; set; }
    public int NumTriangles { get; set; }
    public int NumSubShapes { get; set; }
    public int OriginalSize { get; set; }
    public int NewSize { get; set; }
    public int SizeIncrease => NewSize - OriginalSize;

    /// <summary>Offset within block where compressed vertices start (absolute file offset).</summary>
    public int VertexDataOffset { get; set; }
}

/// <summary>
///     Information about how a NiSkinPartition block needs bone weights/indices expansion.
/// </summary>
internal sealed class SkinPartitionExpansion
{
    public int BlockIndex { get; set; }
    public int OriginalSize { get; set; }
    public int NewSize { get; set; }
    public int SizeIncrease => NewSize - OriginalSize;

    /// <summary>Parsed skin partition data for writing expanded block.</summary>
    public required NifSkinPartitionExpander.SkinPartitionData ParsedData { get; set; }
}

/// <summary>
///     Data stream info from BSPackedAdditionalGeometryData.
/// </summary>
internal sealed class DataStreamInfo
{
    public uint Type { get; set; }
    public uint UnitSize { get; set; }
    public uint TotalSize { get; set; }
    public uint Stride { get; set; }
    public uint BlockIndex { get; set; }
    public uint BlockOffset { get; set; }
    public byte Flags { get; set; }
}

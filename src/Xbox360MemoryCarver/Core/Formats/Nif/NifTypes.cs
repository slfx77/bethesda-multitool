namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Result of a NIF conversion operation.
/// </summary>
public sealed class ConversionResult
{
    public required bool Success { get; init; }
    public byte[]? OutputData { get; init; }
    public string? ErrorMessage { get; init; }
    public NifInfo? SourceInfo { get; init; }
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
    public int OriginalSize { get; set; }
    public int NewSize { get; set; }
    /// <summary>Offset within block where vertices start (after NumVertices + Compressed byte).</summary>
    public int VertexDataOffset { get; set; }
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

namespace NifAnalyzer.Models;

/// <summary>
/// Stream descriptor from BSPackedAdditionalGeometryData (NiAGDDataStream).
/// </summary>
internal class PackedGeomStreamInfo
{
    /// <summary>
    /// Data type (16 = half4, 14 = half2, 28 = ubyte4).
    /// </summary>
    public uint Type { get; set; }

    /// <summary>
    /// Size of each unit in bytes.
    /// </summary>
    public uint UnitSize { get; set; }

    /// <summary>
    /// Total size of stream data.
    /// </summary>
    public uint TotalSize { get; set; }

    /// <summary>
    /// Stride between vertices (typically same for all streams in a block).
    /// </summary>
    public uint Stride { get; set; }

    /// <summary>
    /// Index of the data block containing this stream.
    /// </summary>
    public uint BlockIndex { get; set; }

    /// <summary>
    /// Offset within vertex stride where this stream's data starts.
    /// </summary>
    public uint BlockOffset { get; set; }

    /// <summary>
    /// Stream flags.
    /// </summary>
    public byte Flags { get; set; }

    /// <summary>
    /// Human-readable interpretation of the stream type.
    /// </summary>
    public string GetInterpretation() => (Type, UnitSize) switch
    {
        (16, 8) => "half4 (position/normal/tangent)",
        (14, 4) => "half2 (UV)",
        (28, 4) => "ubyte4 (bone indices/color)",
        _ => "unknown"
    };
}

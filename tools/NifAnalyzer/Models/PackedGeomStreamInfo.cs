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
    /// Human-readable interpretation of the stream type (basic).
    /// </summary>
    public string GetInterpretation() => (Type, UnitSize) switch
    {
        (16, 8) => "half4 (vec3+w)",
        (14, 4) => "half2 (UV)",
        (28, 4) => "ubyte4 (color/indices)",
        _ => "unknown"
    };

    /// <summary>
    /// Stream semantic types for BSPackedAdditionalGeometryData.
    /// Based on analysis of Xbox 360 NIFs - half4 streams ordered by offset:
    /// Position (0), Tangent (8), Bitangent (24), Normal (32).
    /// </summary>
    public enum StreamSemantic
    {
        Unknown,
        Position,
        Tangent,
        Bitangent,
        Normal,
        UV,
        VertexColor,
        BoneIndices
    }

    /// <summary>
    /// Determine semantic type for this stream based on type and position among half4 streams.
    /// IMPORTANT: Based on actual data analysis comparing Xbox 360 vs PC normals:
    /// - Offset 0: Position (varying lengths based on model scale)
    /// - Offset 8: **NORMAL** (length ~1.0, matches PC reference normals)
    /// - Offset 24: Tangent (length ~1.0)
    /// - Offset 32: Bitangent (length ~1.0)
    /// </summary>
    /// <param name="half4Index">Index of this stream among all type=16 half4 streams (sorted by offset).</param>
    public StreamSemantic GetSemantic(int half4Index = -1)
    {
        return (Type, UnitSize) switch
        {
            (14, 4) => StreamSemantic.UV,
            (28, 4) when BlockOffset < 20 => StreamSemantic.VertexColor,
            (28, 4) => StreamSemantic.BoneIndices,
            (16, 8) when half4Index == 0 => StreamSemantic.Position,
            (16, 8) when half4Index == 1 => StreamSemantic.Normal,      // VERIFIED: offset 8 matches PC normals
            (16, 8) when half4Index == 2 => StreamSemantic.Tangent,     // offset 24
            (16, 8) when half4Index == 3 => StreamSemantic.Bitangent,   // offset 32
            (16, 8) => StreamSemantic.Unknown, // 5+ half4 streams - unusual
            _ => StreamSemantic.Unknown
        };
    }

    /// <summary>
    /// Get descriptive name including semantic.
    /// </summary>
    public string GetSemanticName(int half4Index = -1)
    {
        var semantic = GetSemantic(half4Index);
        return semantic switch
        {
            StreamSemantic.Position => "Position (half4)",
            StreamSemantic.Tangent => "Tangent (half4)",
            StreamSemantic.Bitangent => "Bitangent (half4)",
            StreamSemantic.Normal => "Normal (half4)",
            StreamSemantic.UV => "UV (half2)",
            StreamSemantic.VertexColor => "VertexColor (ubyte4)",
            StreamSemantic.BoneIndices => "BoneIndices (ubyte4)",
            _ => GetInterpretation()
        };
    }
}

namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

/// <summary>
///     Stream semantic types for BSPackedAdditionalGeometryData.
///     Based on analysis of Xbox 360 NIFs - half4 streams ordered by offset:
///     Position (0), Tangent (8), Bitangent (24), Normal (32).
/// </summary>
internal enum StreamSemantic
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
///     Data stream info from BSPackedAdditionalGeometryData (NiAGDDataStream).
/// </summary>
internal sealed class DataStreamInfo
{
    /// <summary>
    ///     Data type (16 = half4, 14 = half2, 28 = ubyte4).
    /// </summary>
    public uint Type { get; set; }

    /// <summary>
    ///     Size of each unit in bytes.
    /// </summary>
    public uint UnitSize { get; set; }

    /// <summary>
    ///     Total size of stream data.
    /// </summary>
    public uint TotalSize { get; set; }

    /// <summary>
    ///     Stride between vertices (typically same for all streams in a block).
    /// </summary>
    public uint Stride { get; set; }

    /// <summary>
    ///     Index of the data block containing this stream.
    /// </summary>
    public uint BlockIndex { get; set; }

    /// <summary>
    ///     Offset within vertex stride where this stream's data starts.
    /// </summary>
    public uint BlockOffset { get; set; }

    /// <summary>
    ///     Stream flags.
    /// </summary>
    public byte Flags { get; set; }

    /// <summary>
    ///     Human-readable interpretation of the stream type (basic).
    /// </summary>
    public string GetInterpretation()
    {
        return (Type, UnitSize) switch
        {
            (16, 8) => "half4 (vec3+w)",
            (14, 4) => "half2 (UV)",
            (28, 4) => "ubyte4 (color/indices)",
            _ => "unknown"
        };
    }

    /// <summary>
    ///     Determine semantic type for this stream based on type and position among half4 streams.
    ///     Xbox 360 Packed Geometry Stream Layout (stride 48 bytes):
    ///     - Offset 0:  Position (half4, model-scale values)
    ///     - Offset 8:  Unknown/Auxiliary data (half4, avg length ~0.82-0.90, NOT unit-length)
    ///     - Offset 16: Bone indices (ubyte4) for skinned meshes, or vertex colors
    ///     - Offset 20: Normal (half4, unit-length ~1.0) - VERIFIED against PC reference
    ///     - Offset 28: UV (half2)
    ///     - Offset 32: Tangent (half4, unit-length ~1.0)
    ///     - Offset 40: Bitangent (half4, unit-length ~1.0)
    ///     NOTE: Stream headers may label offset 8 as "Normal" but analysis shows
    ///     actual unit-length normals are at offset 20. The data at offset 8 has
    ///     avg length ~0.82-0.90 and its purpose is not yet fully understood.
    /// </summary>
    /// <param name="half4Index">Index of this stream among all type=16 half4 streams (sorted by offset).</param>
    public StreamSemantic GetSemantic(int half4Index = -1)
    {
        return (Type, UnitSize) switch
        {
            (14, 4) => StreamSemantic.UV,
            (28, 4) when BlockOffset == 16 => StreamSemantic.BoneIndices,
            (28, 4) => StreamSemantic.VertexColor,
            (16, 8) when BlockOffset == 0 => StreamSemantic.Position,
            (16, 8) when BlockOffset == 20 => StreamSemantic.Normal,
            (16, 8) when BlockOffset == 32 => StreamSemantic.Tangent,
            (16, 8) when BlockOffset == 40 => StreamSemantic.Bitangent,
            (16, 8) when BlockOffset == 8 => StreamSemantic.Unknown,
            (16, 8) => StreamSemantic.Unknown,
            _ => StreamSemantic.Unknown
        };
    }

    /// <summary>
    ///     Get descriptive name including semantic.
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

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

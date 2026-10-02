namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     What one channel of a BSPackedAdditionalGeometryData carries. The file stores only a type and a unit size per
///     channel (nif.xml:16435-16455); the semantic is assigned by <see cref="NifPackedGeometryLayout" /> from the
///     measurement of 2026-09-24, never guessed from a vector length.
/// </summary>
internal enum NifPackedStreamKind
{
    /// <summary>The vertex position (the PC Vertices array).</summary>
    Position,

    /// <summary>The vertex normal (the PC Normals array).</summary>
    Normal,

    /// <summary>The vertex color (the PC Vertex Colors array, quantized to bytes).</summary>
    VertexColor,

    /// <summary>UV set 0 (the PC UV Sets row 0).</summary>
    TexCoord,

    /// <summary>
    ///     The bitangent (the PC Bitangents array, nif.xml name), stored at the lower offset of the frame pair. Despite
    ///     the name it runs along +dP/du and is the glTF tangent direction (measured 2026-09-28,
    ///     <see cref="NifModelGeometryData.TangentMappingEvidence" />).
    /// </summary>
    Bitangent,

    /// <summary>
    ///     The tangent (the PC Tangents array), stored at the higher offset of the frame pair. It runs along +dP/dv and
    ///     enters only the glTF handedness (<see cref="NifModelGeometryData.HandednessRule" />).
    /// </summary>
    Tangent,

    /// <summary>The four partition-slot bone weights of a skinned vertex.</summary>
    BoneWeights,

    /// <summary>The four partition-slot bone indices of a skinned vertex.</summary>
    BoneIndices
}

using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Conversion;

/// <summary>One geometry shape's stored tangent frame (<see cref="NifStoredFrameArrays" />).</summary>
/// <param name="ShapeBlock">The shape block index.</param>
/// <param name="DataBlock">The data block index, or -1 when the shape links none.</param>
/// <param name="Packed">True when the data block references a BSPackedAdditionalGeometryData.</param>
/// <param name="VertexCount">The data block's Num Vertices.</param>
/// <param name="Tangents">The first stored array, nif.xml "Tangents", or null.</param>
/// <param name="Bitangents">The second stored array, nif.xml "Bitangents", or null.</param>
internal sealed record NifStoredFrame(int ShapeBlock, int DataBlock, bool Packed, int VertexCount,
    NifFloatArrayValue? Tangents, NifFloatArrayValue? Bitangents);

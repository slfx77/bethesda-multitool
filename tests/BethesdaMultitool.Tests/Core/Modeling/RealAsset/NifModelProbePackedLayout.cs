namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     One measured BSPackedAdditionalGeometryData layout, as the test side transcribes it from the measurement
///     (TestOutput/packed-semantics-20260924/README.md, "Layout table for the C# decoder"): the stream table that
///     identifies it and the channels it carries. Kept apart from the reader's <c>NifPackedGeometryLayout</c> so hop A1
///     matches the probe's stream table against the measurement, not against the code under test.
/// </summary>
/// <param name="Id">The layout id, <c>L1</c> to <c>L6</c>.</param>
/// <param name="Stride">The vertex stride every stream declares.</param>
/// <param name="Streams">Each stream's type, unit size and offset inside the vertex, in table order.</param>
/// <param name="HasVertexColors">True when the layout carries a vertex color stream (L1, L4, L6).</param>
/// <param name="IsSkinned">True when the layout carries bone weights and indices (L3, L4).</param>
internal sealed record NifModelProbePackedLayout(string Id, int Stride,
    IReadOnlyList<(uint Type, uint UnitSize, uint Offset)> Streams, bool HasVertexColors, bool IsSkinned);

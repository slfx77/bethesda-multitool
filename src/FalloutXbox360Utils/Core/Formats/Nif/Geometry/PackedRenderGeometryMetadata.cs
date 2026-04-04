namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

/// <summary>
///     Converter-surfaced packed render metadata for a geometry data block.
/// </summary>
internal sealed class PackedRenderGeometryMetadata
{
    public required int DataBlockIndex { get; init; }
    public required int SkinPartitionBlockIndex { get; init; }
    public required PackedGeometryData PackedGeometry { get; init; }
    public required PackedTopologyData Topology { get; init; }
}

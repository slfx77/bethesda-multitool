namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

/// <summary>
///     Packed-domain topology for skinned Xbox 360 geometry that keeps partition-order
///     vertices alive instead of flattening them back to mesh order.
/// </summary>
internal sealed class PackedTopologyData
{
    public required int MeshVertexCount { get; init; }
    public required int PackedVertexCount { get; init; }
    public required ushort[] VertexMap { get; init; }
    public required ushort[] PackedTriangles { get; init; }

    public PackedTopologyData Clone()
    {
        return new PackedTopologyData
        {
            MeshVertexCount = MeshVertexCount,
            PackedVertexCount = PackedVertexCount,
            VertexMap = (ushort[])VertexMap.Clone(),
            PackedTriangles = (ushort[])PackedTriangles.Clone()
        };
    }
}

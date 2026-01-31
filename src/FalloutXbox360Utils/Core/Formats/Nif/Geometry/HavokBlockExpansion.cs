namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

/// <summary>
///     Information about how a Havok block needs vertex decompression.
/// </summary>
internal sealed class HavokBlockExpansion
{
    public int BlockIndex { get; set; }
    public int NumVertices { get; set; }
    public int NumTriangles { get; set; }
    public int NumSubShapes { get; set; }
    public int OriginalSize { get; set; }
    public int NewSize { get; set; }
    public int SizeIncrease => NewSize - OriginalSize;

    /// <summary>Offset within block where compressed vertices start (absolute file offset).</summary>
    public int VertexDataOffset { get; set; }
}

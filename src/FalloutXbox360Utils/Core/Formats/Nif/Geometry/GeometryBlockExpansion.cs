namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

/// <summary>
///     Information about how a geometry block needs to be expanded.
/// </summary>
internal sealed class GeometryBlockExpansion
{
    public int BlockIndex { get; set; }
    public int PackedBlockIndex { get; set; }
    public int SizeIncrease { get; set; }
    public int OriginalSize { get; set; }
    public int NewSize { get; set; }
    public string BlockTypeName { get; set; } = "";
}

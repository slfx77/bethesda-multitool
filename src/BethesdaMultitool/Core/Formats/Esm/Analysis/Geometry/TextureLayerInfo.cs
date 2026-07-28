namespace BethesdaMultitool.Core.Formats.Esm.Analysis;

/// <summary>One LAND landscape texture layer: its LTEX FormID, quadrant, and layer index.</summary>
public sealed class TextureLayerInfo
{
    public string TextureFormId { get; set; } = "";
    public int Quadrant { get; set; }
    public string QuadrantName { get; set; } = "";
    public int Layer { get; set; }
    public int UnknownByte { get; set; }
}

namespace EsmAnalyzer.Core;

public sealed class WorldmapMetadata
{
    public string Worldspace { get; set; } = "";
    public string FormId { get; set; } = "";
    public int CellsExtracted { get; set; }
    public int CellsTotal { get; set; }
    public GridBounds GridBounds { get; set; } = new();
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public int Scale { get; set; }
    public HeightRange HeightRange { get; set; } = new();
    public bool IsBigEndian { get; set; }
    public string SourceType { get; set; } = "";
    public bool IsRaw16Bit { get; set; }
}
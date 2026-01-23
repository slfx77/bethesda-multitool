namespace EsmAnalyzer.Commands;

// Export data structures
public sealed class LandExportData
{
    public string FormId { get; set; } = "";
    public string Offset { get; set; } = "";
    public int CompressedSize { get; set; }
    public int DecompressedSize { get; set; }
    public bool IsBigEndian { get; set; }
    public uint DataFlags { get; set; }
    public bool HasNormals { get; set; }
    public bool HasHeightmap { get; set; }
    public bool HasVertexColors { get; set; }
    public float BaseHeight { get; set; }
    public TextureLayerInfo? BaseTexture { get; set; }
    public List<TextureLayerInfo>? TextureLayers { get; set; }
    public int VertexTextureEntries { get; set; }
    public List<SubrecordExportInfo> Subrecords { get; set; } = [];
}

public sealed class TextureLayerInfo
{
    public string TextureFormId { get; set; } = "";
    public int Quadrant { get; set; }
    public string QuadrantName { get; set; } = "";
    public int Layer { get; set; }
    public int UnknownByte { get; set; }
}

public sealed class SubrecordExportInfo
{
    public string Signature { get; set; } = "";
    public int Size { get; set; }
}

// Worldmap export structures
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

public sealed class GridBounds
{
    public int MinX { get; set; }
    public int MaxX { get; set; }
    public int MinY { get; set; }
    public int MaxY { get; set; }
}

public sealed class HeightRange
{
    public float Min { get; set; }
    public float Max { get; set; }
}

namespace EsmAnalyzer.Core;

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

// Worldmap export structures
namespace NifAnalyzer.Models;

/// <summary>
///     Contains parsed geometry data from NiTriShapeData/NiTriStripsData blocks.
/// </summary>
internal class GeometryInfo
{
    // NiGeometryData fields
    public int GroupId { get; set; }
    public ushort NumVertices { get; set; }
    public byte KeepFlags { get; set; }
    public byte CompressFlags { get; set; }
    public ushort BsVectorFlags { get; set; }
    public uint MaterialCRC { get; set; }
    public byte HasVertices { get; set; }
    public byte HasNormals { get; set; }
    public float TangentCenterX { get; set; }
    public float TangentCenterY { get; set; }
    public float TangentCenterZ { get; set; }
    public float TangentRadius { get; set; }
    public byte HasVertexColors { get; set; }
    public ushort NumUvSets { get; set; }
    public ushort TSpaceFlag { get; set; }
    public ushort ConsistencyFlags { get; set; }
    public int AdditionalData { get; set; }

    // NiTriBasedGeomData
    public ushort NumTriangles { get; set; }

    // NiTriShapeData specific
    public uint NumTrianglePoints { get; set; }
    public byte HasTriangles { get; set; }
    public ushort NumMatchGroups { get; set; }
    public int TrianglesFieldOffset { get; set; }
    public int ParsedSize { get; set; }

    // NiTriStripsData specific
    public ushort NumStrips { get; set; }
    public ushort[]? StripLengths { get; set; }
    public byte HasPoints { get; set; }

    /// <summary>
    ///     Field offsets for debugging - maps field name to relative block offset.
    /// </summary>
    public Dictionary<string, int> FieldOffsets { get; } = new();
}
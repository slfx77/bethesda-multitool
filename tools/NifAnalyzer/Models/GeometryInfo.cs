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
    public ushort DataFlags { get; set; }
    public bool UsesBsDataFlags { get; set; }
    public uint MaterialCrc { get; set; }
    public uint HasVertices { get; set; }
    public uint HasNormals { get; set; }
    public float BoundingCenterX { get; set; }
    public float BoundingCenterY { get; set; }
    public float BoundingCenterZ { get; set; }
    public float BoundingRadius { get; set; }
    public uint HasVertexColors { get; set; }
    public ushort NumUvSets { get; set; }
    public uint HasUv { get; set; }
    public ushort ConsistencyFlags { get; set; }
    public int AdditionalData { get; set; } = -1;

    // NiTriBasedGeomData
    public ushort NumTriangles { get; set; }

    // NiTriShapeData specific
    public uint NumTrianglePoints { get; set; }
    public uint HasTriangles { get; set; }
    public ushort NumMatchGroups { get; set; }
    public int TrianglesFieldOffset { get; set; }
    public int ParsedSize { get; set; }
    public string? ParseWarning { get; set; }

    // NiTriStripsData specific
    public ushort NumStrips { get; set; }
    public ushort[]? StripLengths { get; set; }
    public uint HasPoints { get; set; }

    /// <summary>
    ///     Field offsets for debugging - maps field name to relative block offset.
    /// </summary>
    public Dictionary<string, int> FieldOffsets { get; } = new();
}

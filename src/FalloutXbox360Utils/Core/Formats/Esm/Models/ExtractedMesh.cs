namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Mesh geometry data extracted from NiTriShapeData or NiTriStripsData runtime objects.
///     Contains vertex positions, normals, UVs, vertex colors, and triangle indices
///     in PC-compatible format (already unpacked from Xbox 360 BSPackedAdditionalGeometryData).
/// </summary>
public record ExtractedMesh
{
    /// <summary>Type of mesh source (NiTriShapeData or NiTriStripsData).</summary>
    public MeshType Type { get; init; }

    /// <summary>Number of vertices in the mesh.</summary>
    public int VertexCount { get; init; }

    /// <summary>Vertex positions as flat [x,y,z, x,y,z, ...] array (3 × VertexCount floats).</summary>
    public required float[] Vertices { get; init; }

    /// <summary>Vertex normals as flat [x,y,z, ...] array, or null if not available.</summary>
    public float[]? Normals { get; init; }

    /// <summary>UV texture coordinates as flat [u,v, ...] array, or null if not available.</summary>
    public float[]? UVs { get; init; }

    /// <summary>Vertex colors as flat [r,g,b,a, ...] array, or null if not available.</summary>
    public float[]? VertexColors { get; init; }

    /// <summary>Triangle indices (3 per triangle), or null if not available.</summary>
    public ushort[]? TriangleIndices { get; init; }

    /// <summary>Number of triangles in the mesh.</summary>
    public int TriangleCount => TriangleIndices != null ? TriangleIndices.Length / 3 : 0;

    /// <summary>File offset where the NiGeometryData struct was found.</summary>
    public long SourceOffset { get; init; }

    /// <summary>Hash of vertex data for deduplication.</summary>
    public long VertexHash { get; init; }

    /// <summary>Bounding sphere center X.</summary>
    public float BoundCenterX { get; init; }

    /// <summary>Bounding sphere center Y.</summary>
    public float BoundCenterY { get; init; }

    /// <summary>Bounding sphere center Z.</summary>
    public float BoundCenterZ { get; init; }

    /// <summary>Bounding sphere radius.</summary>
    public float BoundRadius { get; init; }

    /// <summary>
    ///     True if this mesh has vertex normals, indicating 3D lit geometry.
    ///     Meshes without normals are typically 2D UI/HUD elements (text, buttons, icons).
    /// </summary>
    public bool Is3D => Normals != null;
}

/// <summary>Source mesh type.</summary>
public enum MeshType
{
    /// <summary>NiTriShapeData — indexed triangle list.</summary>
    TriShape,

    /// <summary>NiTriStripsData — triangle strips (converted to triangle list on export).</summary>
    TriStrips
}

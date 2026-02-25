namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     All renderable geometry extracted from a single NIF file,
///     with node transforms applied so all positions are in model space.
/// </summary>
internal sealed class NifRenderableModel
{
    public List<RenderableSubmesh> Submeshes { get; } = [];
    public float MinX { get; set; } = float.MaxValue;
    public float MinY { get; set; } = float.MaxValue;
    public float MinZ { get; set; } = float.MaxValue;
    public float MaxX { get; set; } = float.MinValue;
    public float MaxY { get; set; } = float.MinValue;
    public float MaxZ { get; set; } = float.MinValue;

    /// <summary>
    ///     Update bounding box from a submesh's positions.
    /// </summary>
    public void ExpandBounds(float[] positions)
    {
        for (var i = 0; i < positions.Length; i += 3)
        {
            var x = positions[i];
            var y = positions[i + 1];
            var z = positions[i + 2];
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (z < MinZ) MinZ = z;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
            if (z > MaxZ) MaxZ = z;
        }
    }

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;
    public float Depth => MaxZ - MinZ;
    public bool HasGeometry => Submeshes.Count > 0;
}

/// <summary>
///     One geometry block's renderable data with transforms already applied.
/// </summary>
internal sealed class RenderableSubmesh
{
    /// <summary>X, Y, Z per vertex (length = numVertices * 3).</summary>
    public required float[] Positions { get; init; }

    /// <summary>3 indices per triangle (length = numTriangles * 3).</summary>
    public required ushort[] Triangles { get; init; }

    /// <summary>X, Y, Z per vertex (optional, for shading). Same length as Positions.</summary>
    public float[]? Normals { get; init; }

    /// <summary>U, V per vertex (optional, for texture mapping). Length = numVertices * 2.</summary>
    public float[]? UVs { get; init; }

    /// <summary>R, G, B, A per vertex (optional). Length = numVertices * 4.</summary>
    public byte[]? VertexColors { get; init; }

    /// <summary>Tangent X, Y, Z per vertex (optional, for bump mapping). Same length as Positions.</summary>
    public float[]? Tangents { get; init; }

    /// <summary>Bitangent X, Y, Z per vertex (optional, for bump mapping). Same length as Positions.</summary>
    public float[]? Bitangents { get; init; }

    /// <summary>Diffuse texture path resolved from shader properties (e.g., "textures\architecture\foo.dds").</summary>
    public string? DiffuseTexturePath { get; init; }

    /// <summary>Normal map texture path resolved from shader properties (slot 1).</summary>
    public string? NormalMapTexturePath { get; init; }

    /// <summary>True if this submesh uses BSShaderNoLightingProperty (self-illuminated, e.g., neon signs).</summary>
    public bool IsEmissive { get; init; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Triangles.Length / 3;
}

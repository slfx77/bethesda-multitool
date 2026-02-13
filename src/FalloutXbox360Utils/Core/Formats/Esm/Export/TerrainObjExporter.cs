using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Exports runtime terrain mesh data to Wavefront OBJ format for visual verification.
///     The 33×33 vertex grid is triangulated into 2×32×32 = 2048 triangles.
/// </summary>
public static class TerrainObjExporter
{
    /// <summary>Cell size in world units (each grid cell is 4096×4096).</summary>
    private const float CellSize = 4096.0f;

    /// <summary>
    ///     Export a single cell's terrain mesh to OBJ format.
    ///     Coordinates are centered around the origin for easy viewing in 3D tools.
    /// </summary>
    public static void Export(RuntimeTerrainMesh mesh, int cellX, int cellY, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Terrain mesh for cell [{cellX}, {cellY}]");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# {RuntimeTerrainMesh.VertexCount} vertices, {RuntimeTerrainMesh.GridSize}x{RuntimeTerrainMesh.GridSize} grid");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Centered at origin (world cell: [{cellX}, {cellY}])");
        sb.AppendLine();

        // Center at origin — vertex data already contains local positions within the cell
        var worldOffsetX = 0.0f;
        var worldOffsetY = 0.0f;

        AppendVertices(sb, mesh, worldOffsetX, worldOffsetY);
        AppendNormals(sb, mesh);
        AppendFaces(sb, mesh.HasNormals);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>
    ///     Export multiple cells' terrain meshes to a single OBJ file.
    ///     Coordinates are centered around the origin for easy viewing in 3D tools.
    /// </summary>
    public static void ExportMultiple(
        IEnumerable<(RuntimeTerrainMesh Mesh, int CellX, int CellY)> cells, string outputPath)
    {
        var cellList = cells.ToList();

        // Compute centroid of all cell world positions to center the mesh at origin
        var centerX = (float)(cellList.Average(c => c.CellX) * CellSize + CellSize / 2);
        var centerY = (float)(cellList.Average(c => c.CellY) * CellSize + CellSize / 2);

        var sb = new StringBuilder();
        sb.AppendLine("# Multi-cell terrain mesh export");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Centered at origin (world offset: {centerX:F0}, {centerY:F0})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Cell range: [{cellList.Min(c => c.CellX)},{cellList.Min(c => c.CellY)}] to [{cellList.Max(c => c.CellX)},{cellList.Max(c => c.CellY)}]");
        sb.AppendLine();

        var vertexOffset = 0;
        var normalOffset = 0;

        foreach (var (mesh, cellX, cellY) in cellList)
        {
            var worldOffsetX = cellX * CellSize - centerX;
            var worldOffsetY = cellY * CellSize - centerY;

            sb.AppendLine(CultureInfo.InvariantCulture, $"g cell_{cellX}_{cellY}");

            AppendVertices(sb, mesh, worldOffsetX, worldOffsetY);
            AppendNormals(sb, mesh);
            AppendFaces(sb, mesh.HasNormals, vertexOffset, normalOffset);

            vertexOffset += RuntimeTerrainMesh.VertexCount;
            if (mesh.HasNormals)
            {
                normalOffset += RuntimeTerrainMesh.VertexCount;
            }

            sb.AppendLine();
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    private static void AppendVertices(
        StringBuilder sb, RuntimeTerrainMesh mesh, float worldOffsetX, float worldOffsetY)
    {
        for (var i = 0; i < RuntimeTerrainMesh.VertexCount; i++)
        {
            var x = mesh.Vertices[i * 3] + worldOffsetX;
            var y = mesh.Vertices[i * 3 + 1] + worldOffsetY;
            var z = mesh.Vertices[i * 3 + 2];
            sb.AppendLine(CultureInfo.InvariantCulture, $"v {x:F4} {z:F4} {y:F4}");
        }
    }

    private static void AppendNormals(StringBuilder sb, RuntimeTerrainMesh mesh)
    {
        if (!mesh.HasNormals)
        {
            return;
        }

        for (var i = 0; i < RuntimeTerrainMesh.VertexCount; i++)
        {
            var nx = mesh.Normals![i * 3];
            var ny = mesh.Normals[i * 3 + 1];
            var nz = mesh.Normals[i * 3 + 2];
            sb.AppendLine(CultureInfo.InvariantCulture, $"vn {nx:F4} {nz:F4} {ny:F4}");
        }
    }

    private static void AppendFaces(
        StringBuilder sb, bool hasNormals, int vertexOffset = 0, int normalOffset = 0)
    {
        // Triangulate the 33×33 grid: 2 triangles per quad, 32×32 quads
        for (var row = 0; row < RuntimeTerrainMesh.GridSize - 1; row++)
        {
            for (var col = 0; col < RuntimeTerrainMesh.GridSize - 1; col++)
            {
                // OBJ indices are 1-based
                var topLeft = row * RuntimeTerrainMesh.GridSize + col + 1 + vertexOffset;
                var topRight = topLeft + 1;
                var bottomLeft = topLeft + RuntimeTerrainMesh.GridSize;
                var bottomRight = bottomLeft + 1;

                if (hasNormals)
                {
                    var nTopLeft = row * RuntimeTerrainMesh.GridSize + col + 1 + normalOffset;
                    var nTopRight = nTopLeft + 1;
                    var nBottomLeft = nTopLeft + RuntimeTerrainMesh.GridSize;
                    var nBottomRight = nBottomLeft + 1;

                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"f {topLeft}//{nTopLeft} {bottomLeft}//{nBottomLeft} {bottomRight}//{nBottomRight}");
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"f {topLeft}//{nTopLeft} {bottomRight}//{nBottomRight} {topRight}//{nTopRight}");
                }
                else
                {
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"f {topLeft} {bottomLeft} {bottomRight}");
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"f {topLeft} {bottomRight} {topRight}");
                }
            }
        }
    }
}

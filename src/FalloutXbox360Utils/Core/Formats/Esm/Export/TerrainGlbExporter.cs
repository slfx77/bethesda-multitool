using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Exports runtime terrain mesh data to glTF Binary (.glb) format via the shared
///     <see cref="GlbWriter" /> pipeline. The 33x33 vertex grid is triangulated into
///     2 * 32 * 32 = 2048 triangles per cell.
/// </summary>
internal static class TerrainGlbExporter
{
    private const float CellSize = 4096.0f;

    internal static void Export(RuntimeTerrainMesh mesh, int cellX, int cellY, string outputPath)
    {
        var scene = new GlbScene();
        AddCellPart(scene, mesh, $"cell_{cellX}_{cellY}", 0f, 0f);
        WriteScene(scene, outputPath);
    }

    internal static void ExportMultiple(
        IEnumerable<(RuntimeTerrainMesh Mesh, int CellX, int CellY)> cells, string outputPath)
    {
        var cellList = cells.ToList();
        var centerX = (float)(cellList.Average(c => c.CellX) * CellSize + CellSize / 2);
        var centerY = (float)(cellList.Average(c => c.CellY) * CellSize + CellSize / 2);

        var scene = new GlbScene();
        foreach (var (mesh, cellX, cellY) in cellList)
        {
            var offsetX = cellX * CellSize - centerX;
            var offsetY = cellY * CellSize - centerY;
            AddCellPart(scene, mesh, $"cell_{cellX}_{cellY}", offsetX, offsetY);
        }

        WriteScene(scene, outputPath);
    }

    private static void AddCellPart(
        GlbScene scene, RuntimeTerrainMesh mesh, string name, float offsetX, float offsetY)
    {
        var positions = BuildOffsetPositions(mesh, offsetX, offsetY);
        var triangles = BuildGridTriangles();

        var submesh = new RenderableSubmesh
        {
            ShapeName = name,
            Positions = positions,
            Triangles = triangles,
            Normals = mesh.HasNormals ? mesh.Normals : null
        };

        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = name,
            NodeIndex = GlbScene.RootNodeIndex,
            Submesh = submesh
        });
    }

    private static float[] BuildOffsetPositions(RuntimeTerrainMesh mesh, float offsetX, float offsetY)
    {
        var output = new float[RuntimeTerrainMesh.VertexCount * 3];
        for (var i = 0; i < RuntimeTerrainMesh.VertexCount; i++)
        {
            output[i * 3] = mesh.Vertices[i * 3] + offsetX;
            output[i * 3 + 1] = mesh.Vertices[i * 3 + 1] + offsetY;
            output[i * 3 + 2] = mesh.Vertices[i * 3 + 2];
        }

        return output;
    }

    private static ushort[] BuildGridTriangles()
    {
        const int gridSize = RuntimeTerrainMesh.GridSize;
        var quadsPerSide = gridSize - 1;
        var triangles = new ushort[quadsPerSide * quadsPerSide * 6];
        var i = 0;
        for (var row = 0; row < quadsPerSide; row++)
        {
            for (var col = 0; col < quadsPerSide; col++)
            {
                var topLeft = (ushort)(row * gridSize + col);
                var topRight = (ushort)(topLeft + 1);
                var bottomLeft = (ushort)(topLeft + gridSize);
                var bottomRight = (ushort)(bottomLeft + 1);

                triangles[i++] = topLeft;
                triangles[i++] = bottomLeft;
                triangles[i++] = bottomRight;
                triangles[i++] = topLeft;
                triangles[i++] = bottomRight;
                triangles[i++] = topRight;
            }
        }

        return triangles;
    }

    private static void WriteScene(GlbScene scene, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var textureResolver = new NifTextureResolver();
        GlbWriter.Write(scene, textureResolver, outputPath);
    }
}

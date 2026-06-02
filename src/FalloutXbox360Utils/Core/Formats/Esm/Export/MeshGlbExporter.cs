using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Exports extracted mesh data to glTF Binary (.glb) format via the shared
///     <see cref="GlbWriter" /> pipeline. Replaces the legacy Wavefront OBJ exporter.
///     The companion CSV summary helper lives alongside since it shares the same input set.
/// </summary>
internal static class MeshGlbExporter
{
    internal static void Export(ExtractedMesh mesh, string outputPath, string? objectName = null)
    {
        var name = objectName ?? $"mesh_{mesh.SourceOffset:X}";
        var scene = new GlbScene();
        AddMeshPart(scene, mesh, name);
        WriteScene(scene, outputPath);
    }

    internal static void ExportMultiple(IReadOnlyList<ExtractedMesh> meshes, string outputPath)
    {
        ExportMultiple(meshes, outputPath, null, null);
    }

    internal static void ExportMultiple(
        IReadOnlyList<ExtractedMesh> meshes,
        string outputPath,
        Dictionary<long, SceneGraphInfo>? sceneGraph,
        Dictionary<string, string>? modelNameIndex)
    {
        var scene = new GlbScene();
        for (var i = 0; i < meshes.Count; i++)
        {
            var name = AssetNameResolver.ResolveMeshName(meshes[i], i, sceneGraph, modelNameIndex);
            AddMeshPart(scene, meshes[i], name);
        }

        WriteScene(scene, outputPath);
    }

    internal static void ExportSummary(IReadOnlyList<ExtractedMesh> meshes, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Index,Offset,Type,Category,Vertices,Triangles,HasNormals,HasUVs,HasColors," +
                      "BoundCenterX,BoundCenterY,BoundCenterZ,BoundRadius");

        for (var i = 0; i < meshes.Count; i++)
        {
            var m = meshes[i];
            var category = m.Is3D ? "3D" : "UI";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{i},0x{m.SourceOffset:X},{m.Type},{category},{m.VertexCount},{m.TriangleCount}," +
                $"{(m.Normals != null ? "Yes" : "No")},{(m.UVs != null ? "Yes" : "No")}," +
                $"{(m.VertexColors != null ? "Yes" : "No")}," +
                $"{m.BoundCenterX:F2},{m.BoundCenterY:F2},{m.BoundCenterZ:F2},{m.BoundRadius:F2}");
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    private static void AddMeshPart(GlbScene scene, ExtractedMesh mesh, string name)
    {
        if (mesh.TriangleIndices is not { Length: >= 3 } indices)
        {
            return;
        }

        var submesh = new RenderableSubmesh
        {
            ShapeName = name,
            Positions = mesh.Vertices,
            Triangles = indices,
            Normals = mesh.Normals,
            UVs = mesh.UVs
        };

        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = name,
            NodeIndex = GlbScene.RootNodeIndex,
            Submesh = submesh
        });
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

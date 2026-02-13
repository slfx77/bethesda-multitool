using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Exports extracted mesh data to Wavefront OBJ format.
///     Handles arbitrary meshes with vertices, normals, UVs, and triangle indices.
///     Axis convention matches TerrainObjExporter: Bethesda Y-forward → OBJ (swap Y/Z).
/// </summary>
public static class MeshObjExporter
{
    /// <summary>
    ///     Export a single mesh to an OBJ file.
    /// </summary>
    public static void Export(ExtractedMesh mesh, string outputPath, string? objectName = null)
    {
        var sb = new StringBuilder();
        var name = objectName ?? $"mesh_{mesh.SourceOffset:X}";

        sb.AppendLine(CultureInfo.InvariantCulture, $"# Extracted mesh: {name}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles, type: {mesh.Type}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Source offset: 0x{mesh.SourceOffset:X}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Bound: center ({mesh.BoundCenterX:F1}, {mesh.BoundCenterY:F1}, {mesh.BoundCenterZ:F1}) radius {mesh.BoundRadius:F1}");
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture, $"o {name}");
        AppendVertices(sb, mesh);
        AppendUVs(sb, mesh);
        AppendNormals(sb, mesh);
        AppendFaces(sb, mesh, 0, 0, 0);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>
    ///     Export multiple meshes to a single OBJ file with named objects.
    /// </summary>
    public static void ExportMultiple(IReadOnlyList<ExtractedMesh> meshes, string outputPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Extracted meshes: {meshes.Count} objects");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Total vertices: {meshes.Sum(m => m.VertexCount):N0}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"# Total triangles: {meshes.Sum(m => m.TriangleCount):N0}");
        sb.AppendLine();

        var vertexOffset = 0;
        var normalOffset = 0;
        var uvOffset = 0;

        for (var i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"o mesh_{i:D4}_{mesh.SourceOffset:X}");

            AppendVertices(sb, mesh);
            AppendUVs(sb, mesh);
            AppendNormals(sb, mesh);
            AppendFaces(sb, mesh, vertexOffset, normalOffset, uvOffset);

            vertexOffset += mesh.VertexCount;
            if (mesh.Normals != null)
            {
                normalOffset += mesh.VertexCount;
            }

            if (mesh.UVs != null)
            {
                uvOffset += mesh.VertexCount;
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

    /// <summary>
    ///     Export a mesh summary CSV alongside the OBJ files.
    /// </summary>
    public static void ExportSummary(IReadOnlyList<ExtractedMesh> meshes, string outputPath)
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

    private static void AppendVertices(StringBuilder sb, ExtractedMesh mesh)
    {
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var x = mesh.Vertices[i * 3];
            var y = mesh.Vertices[i * 3 + 1];
            var z = mesh.Vertices[i * 3 + 2];
            // Swap Y and Z for Bethesda → OBJ coordinate convention
            sb.AppendLine(CultureInfo.InvariantCulture, $"v {x:F6} {z:F6} {y:F6}");
        }
    }

    private static void AppendNormals(StringBuilder sb, ExtractedMesh mesh)
    {
        if (mesh.Normals == null)
        {
            return;
        }

        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var nx = mesh.Normals[i * 3];
            var ny = mesh.Normals[i * 3 + 1];
            var nz = mesh.Normals[i * 3 + 2];
            sb.AppendLine(CultureInfo.InvariantCulture, $"vn {nx:F6} {nz:F6} {ny:F6}");
        }
    }

    private static void AppendUVs(StringBuilder sb, ExtractedMesh mesh)
    {
        if (mesh.UVs == null)
        {
            return;
        }

        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var u = mesh.UVs[i * 2];
            var v = mesh.UVs[i * 2 + 1];
            // Flip V for DirectX → OpenGL convention
            sb.AppendLine(CultureInfo.InvariantCulture, $"vt {u:F6} {1.0f - v:F6}");
        }
    }

    private static void AppendFaces(
        StringBuilder sb, ExtractedMesh mesh, int vertexOffset, int normalOffset, int uvOffset)
    {
        if (mesh.TriangleIndices == null || mesh.TriangleIndices.Length < 3)
        {
            return;
        }

        var hasNormals = mesh.Normals != null;
        var hasUVs = mesh.UVs != null;

        for (var i = 0; i < mesh.TriangleIndices.Length; i += 3)
        {
            // OBJ indices are 1-based
            var a = mesh.TriangleIndices[i] + 1 + vertexOffset;
            var b = mesh.TriangleIndices[i + 1] + 1 + vertexOffset;
            var c = mesh.TriangleIndices[i + 2] + 1 + vertexOffset;

            if (hasNormals && hasUVs)
            {
                var na = mesh.TriangleIndices[i] + 1 + normalOffset;
                var nb = mesh.TriangleIndices[i + 1] + 1 + normalOffset;
                var nc = mesh.TriangleIndices[i + 2] + 1 + normalOffset;
                var ta = mesh.TriangleIndices[i] + 1 + uvOffset;
                var tb = mesh.TriangleIndices[i + 1] + 1 + uvOffset;
                var tc = mesh.TriangleIndices[i + 2] + 1 + uvOffset;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"f {a}/{ta}/{na} {b}/{tb}/{nb} {c}/{tc}/{nc}");
            }
            else if (hasNormals)
            {
                var na = mesh.TriangleIndices[i] + 1 + normalOffset;
                var nb = mesh.TriangleIndices[i + 1] + 1 + normalOffset;
                var nc = mesh.TriangleIndices[i + 2] + 1 + normalOffset;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"f {a}//{na} {b}//{nb} {c}//{nc}");
            }
            else if (hasUVs)
            {
                var ta = mesh.TriangleIndices[i] + 1 + uvOffset;
                var tb = mesh.TriangleIndices[i + 1] + 1 + uvOffset;
                var tc = mesh.TriangleIndices[i + 2] + 1 + uvOffset;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"f {a}/{ta} {b}/{tb} {c}/{tc}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"f {a} {b} {c}");
            }
        }
    }
}

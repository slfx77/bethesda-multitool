using System.Globalization;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>
///     Writes a decoded <see cref="VanBurenB3DFile" /> as a GLB, the way
///     <c>BrotherhoodOfSteel.BosXboxMeshGlbExporter</c> and <c>Xngine.Mesh.XnGineMeshGlbExporter</c>
///     do: a <see cref="SceneBuilder" /> holding one rigid mesh per B3D mesh, one primitive per
///     triangle group, built from <c>MeshBuilder&lt;VertexPositionNormal, VertexTexture1, VertexEmpty&gt;</c>.
///     <para>
///         ⚑ <b>Y is already up</b>, so Y passes through untouched. Measured 2026-09-08 on the
///         881 converted (character/item/prop) meshes: the Y minimum is ≥ 0 — the mesh stands on the
///         floor — on <b>715 of 881</b>, against <b>10 of 881</b> for X and <b>82 of 881</b> for Z,
///         which straddle zero. The source-form population agrees the odd axis is Y (tiles fill the
///         positive XZ quadrant: X min ≥ 0 on 2,916/3,031, Z on 2,923/3,031, Y on 202/3,031).
///     </para>
///     <para>
///         ⚑⚑ <b>The stored frame is LEFT-handed and the export mirrors Z and reverses the winding.</b>
///         Three witnesses, none of them the exporter: (1) every retail scene's token-<c>05</c> string
///         is <c>LEFT_HANDED</c> (3,912/3,912), and the engine READS it — <c>FUN_00529490</c> compares
///         that string against <c>"LEFT_HANDED"</c> (<c>0x671f54</c>) and, only when it does NOT match,
///         negates the node translation's third component and the rotation axis before building its
///         D3D transform; (2) on the 135 skinned meshes with both <c>L …</c> and <c>R …</c> bones the
///         vertices weighted to the <c>L</c> bones lie at −X on 135/135, and every body whose head
///         sits ≥ 0.5 units from its pelvis along Z (the 15 quadrupeds; the 75 bipeds are vertically
///         stacked and give no reading) faces +Z, 15/15 — a body facing +Z with its left at −X is
///         left-handed (in a right-handed frame its left would be +X); (3) the triangle winding is
///         one-signed: <c>cross(b−a, c−a) · n</c> on the raw coordinates is &gt; 0 on 814,750 of 816,372
///         triangles (1,526 &lt; 0, 96 degenerate), the majority on 3,912/3,912 meshes — i.e. seen from
///         outside the faces wind CLOCKWISE in the stored left-handed frame (Direct3D's default front).
///         glTF is right-handed with counter-clockwise front faces, so the exporter writes
///         (x, y, −z), (nx, ny, −nz) and (a, c, b): the Z mirror is the axis the engine's own
///         handedness correction negates, and the index swap keeps the normals on the front side.
///         ⚠ Which of X and Z to mirror is a convention either way; Z follows the engine. ⚠ The
///         material stays double-sided because the per-group cull mode lives in the 60-byte render
///         state block the reader consumes by size only.
///     </para>
///     <para>
///         ⚠ Node transforms never occur on retail meshes (0 of 3,912 carry a <c>0x0D</c> token), so
///         none is applied. ⚠ Skinning is exposed on the model but NOT written: the GLB is rigid, in
///         bind pose. ⚠ No texture is attached: a group names its <c>.tga</c>, but the build's image
///         payloads are nameless (see <see cref="VanBurenImageFile" />), so the name cannot be resolved.
///         The material is named after the texture so a viewer at least shows which one is wanted.
///     </para>
/// </summary>
internal static class VanBurenB3DGlbExporter
{
    /// <summary>Writes every mesh of the file to <paramref name="outputPath" />, creating the directory if needed.</summary>
    public static void Write(VanBurenB3DFile file, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Build(file).SaveGLB(outputPath);
    }

    /// <summary>The same export, as bytes.</summary>
    public static byte[] WriteToBytes(VanBurenB3DFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        using var stream = new MemoryStream();
        Build(file).WriteGLB(stream);
        return stream.ToArray();
    }

    private static ModelRoot Build(VanBurenB3DFile file)
    {
        var scene = new SceneBuilder(file.MeshName ?? file.Name);
        var index = 0;
        foreach (var mesh in file.Meshes)
        {
            scene.AddRigidMesh(BuildMesh(mesh, index++), Matrix4x4.Identity);
        }

        return scene.ToGltf2();
    }

    private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> BuildMesh(VanBurenB3DMesh mesh,
        int index)
    {
        var name = string.IsNullOrEmpty(mesh.NodeName)
            ? string.Create(CultureInfo.InvariantCulture, $"mesh_{index}")
            : mesh.NodeName;
        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(name);
        var groupIndex = 0;
        foreach (var group in mesh.Groups)
        {
            var materialName = group.Textures.Count > 0 ? group.Textures[0] : group.Name;
            if (group.Textures.Count == 0 && string.IsNullOrEmpty(materialName))
            {
                materialName = string.Create(CultureInfo.InvariantCulture, $"group_{groupIndex}");
            }
            var material = new MaterialBuilder(materialName)
                .WithDoubleSide(true)
                .WithMetallicRoughnessShader()
                .WithMetallicRoughness(0f, 1f);
            if (string.Equals(group.BlendState, "ALPHABLEND", StringComparison.OrdinalIgnoreCase)
                || string.Equals(group.BlendState, "ADD", StringComparison.OrdinalIgnoreCase))
            {
                material.WithAlpha(AlphaMode.BLEND);
            }

            var primitive = builder.UsePrimitive(material);
            var indices = group.Indices;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                // Left-handed source, right-handed target: mirror Z (in Vertex) and swap b/c.
                primitive.AddTriangle(
                    Vertex(mesh.Vertices[indices[i]]),
                    Vertex(mesh.Vertices[indices[i + 2]]),
                    Vertex(mesh.Vertices[indices[i + 1]]));
            }

            groupIndex++;
        }

        return builder;
    }

    private static (VertexPositionNormal, VertexTexture1) Vertex(VanBurenB3DVertex vertex)
    {
        // Every retail normal is unit length (1,275,078/1,275,078), so this only guards synthetic input.
        var normal = vertex.Normal;
        if (normal.LengthSquared() <= 0)
        {
            normal = Vector3.UnitY;
        }

        normal = Vector3.Normalize(normal);
        var p = vertex.Position;
        return (
            new VertexPositionNormal(new Vector3(p.X, p.Y, -p.Z), new Vector3(normal.X, normal.Y, -normal.Z)),
            new VertexTexture1(vertex.TexCoord));
    }
}

using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Writes a <see cref="BosXboxMesh" /> as a GLB, the same way
///     <c>Xngine.Mesh.XnGineMeshGlbExporter</c> writes the Daggerfall/Redguard meshes: a
///     <see cref="SceneBuilder" /> holding one rigid mesh built from
///     <c>MeshBuilder&lt;VertexPositionNormal, VertexTexture1, VertexEmpty&gt;</c>.
///     <para>
///         ⚑ <b>The game is Z-UP</b>, so a vertex goes out as <c>(x, z, −y)</c>. Three independent
///         populations say so. ⚠ Every figure below was RE-MEASURED on 2026-09-08 because the first
///         pass misquoted two of them; the two corrections are called out where they sit.
///     </para>
///     <para>
///         (1) <b>Meshes sit ON the ground plane, never through it.</b> 75 of the 92
///         <c>armor.clp</c> meshes and 72 of the 153 <c>global.clp</c> meshes have a Z minimum
///         <b>≥ 0</b> — nothing below the floor — against <b>0 of 92</b> and <b>0 of 153</b> for
///         X and for Y, which straddle zero on every single mesh. That is the discriminator, and it
///         could have failed on any of the three axes. ⚠ The stricter reading, a Z minimum of
///         EXACTLY 0, holds on only <b>2 of 92</b> and <b>38 of 153</b> — this comment claimed 75
///         and 72 for it until 2026-09-08, which was the <c>≥ 0</c> count misdescribed.
///     </para>
///     <para>
///         (2) <b>The camera's third coordinate is the tight, vertical one.</b> Over the 305 camera
///         position records in the 29 tiling <c>.cut</c> scripts (<see cref="BosCutscene" />;
///         48 <c>SetPosition</c> + 257 <c>KeyPosition</c>) the third component spans
///         <b>−100 … 984</b> (σ 95; <c>SetPosition</c> alone 74 … 491) while the first two span
///         <b>−2,991 … 3,297</b> (σ 1,284) and <b>−3,505 … 2,845</b> (σ 920) — a camera moves far
///         horizontally and little vertically. ⚠ This comment quoted "129 … 357" until 2026-09-08;
///         no subset of the shipped records produces that range.
///     </para>
///     <para>
///         (3) <b>Cutscene actors are authored standing on the floor.</b> Of the 263 object-table
///         start positions in the same 29 scripts, <b>177 (67%)</b> have a third component within
///         2 units of zero and the whole lane spans only <b>−12.8 … 120.2</b>, against
///         <b>
///             1 of
///             263
///         </b>
///         for each of the first two, which span ±3,000. This population is independent of
///         both of the others — different file, different structure, authored by hand in the
///         editor.
///     </para>
///     <para>
///         The <c>(x, z, −y)</c> rotation has determinant +1, so the triangle winding is carried
///         through unchanged.
///     </para>
///     <para>
///         ⚠ The winding CONVENTION is not established — nothing read off the disc says whether the
///         game's front faces are clockwise — so the material is double-sided and the strip order is
///         passed through as <see cref="BosXboxMesh.TriangleIndices" /> produced it.
///         ⚠ No texture is attached: a mesh section names none, and which clump texture belongs to
///         it is undecoded.
///     </para>
/// </summary>
internal static class BosXboxMeshGlbExporter
{
    /// <summary>Writes one mesh to <paramref name="outputPath" />, creating the directory if needed.</summary>
    public static void Write(BosXboxMesh mesh, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Build(mesh).SaveGLB(outputPath);
    }

    /// <summary>The same export, as bytes.</summary>
    public static byte[] WriteToBytes(BosXboxMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        using var stream = new MemoryStream();
        Build(mesh).WriteGLB(stream);
        return stream.ToArray();
    }

    /// <summary>The game's Z-up position in glTF's Y-up axes.</summary>
    public static Vector3 ToGltf(Vector3 position)
    {
        return new Vector3(position.X, position.Z, -position.Y);
    }

    private static ModelRoot Build(BosXboxMesh mesh)
    {
        var scene = new SceneBuilder(mesh.Name);
        scene.AddRigidMesh(BuildMesh(mesh), Matrix4x4.Identity);
        return scene.ToGltf2();
    }

    private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> BuildMesh(BosXboxMesh mesh)
    {
        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(mesh.Name);
        var material = new MaterialBuilder(mesh.Name)
            .WithDoubleSide(true)
            .WithMetallicRoughnessShader()
            .WithMetallicRoughness(0f, 1f);
        var primitive = builder.UsePrimitive(material);

        for (var i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
        {
            primitive.AddTriangle(
                Vertex(mesh.Vertices[mesh.TriangleIndices[i]]),
                Vertex(mesh.Vertices[mesh.TriangleIndices[i + 1]]),
                Vertex(mesh.Vertices[mesh.TriangleIndices[i + 2]]));
        }

        return builder;
    }

    private static (VertexPositionNormal, VertexTexture1) Vertex(BosXboxMeshVertex vertex)
    {
        // A squared length is never negative, so "not positive" is exactly "no normal authored" —
        // 6 of the 143,439 shipped vertices store an all-zero normal.
        var normal = ToGltf(vertex.Normal);
        if (normal.LengthSquared() <= 0)
        {
            normal = Vector3.UnitY;
        }

        return (
            new VertexPositionNormal(ToGltf(vertex.Position), Vector3.Normalize(normal)),
            new VertexTexture1(vertex.TexCoord));
    }
}

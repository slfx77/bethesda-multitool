using System.Globalization;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace BethesdaMultitool.Core.Formats.Xngine.Mesh;

/// <summary>
///     A decoded texture as PNG bytes plus its pixel size, used to normalise texel UVs. An optional
///     <paramref name="Name" /> names the GLB material (a Battlespire BSI stem such as
///     <c>wall35</c>); without one the material is named by its (archive, record) pair.
/// </summary>
internal sealed record XnGineTexturePng(byte[] Png, int Width, int Height, string? Name = null);

/// <summary>One placed copy of a mesh: the mesh and its transform in the game's Y-down space.</summary>
internal sealed record XnGineMeshInstance(XnGineTriangleMesh Mesh, Matrix4x4 Transform, string Name);

/// <summary>
///     Writes a decomposed XnGine mesh as a GLB: one primitive per texture, materials named after
///     the texture archive/record, base-colour images attached when a provider supplies them.
///     glTF is Y-up and the meshes are Y-down, so Y is negated and the winding reversed; texel
///     UVs divide by the texture size (or the game's usual 64 when no texture is available).
/// </summary>
internal static class XnGineMeshGlbExporter
{
    /// <summary>Texel size assumed for UV normalisation when no texture is supplied.</summary>
    public const int DefaultTextureSize = 64;

    public static void Write(
        XnGineTriangleMesh mesh,
        string outputPath,
        Func<int, int, XnGineTexturePng?>? textureProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Build(mesh, textureProvider).SaveGLB(outputPath);
    }

    public static byte[] WriteToBytes(XnGineTriangleMesh mesh,
        Func<int, int, XnGineTexturePng?>? textureProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        using var stream = new MemoryStream();
        Build(mesh, textureProvider).WriteGLB(stream);
        return stream.ToArray();
    }

    /// <summary>
    ///     Writes a whole scene: many placed instances of (possibly shared) meshes, each with its
    ///     own transform. The transform is expected in the meshes' own Y-down space; the flip to
    ///     glTF's Y-up is applied here by conjugating it, so vertices go out unflipped and one mesh
    ///     can be instanced many times.
    /// </summary>
    public static void WriteScene(
        string sceneName,
        IEnumerable<XnGineMeshInstance> instances,
        string outputPath,
        Func<int, int, XnGineTexturePng?>? textureProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sceneName);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        BuildScene(sceneName, instances, textureProvider).SaveGLB(outputPath);
    }

    /// <summary>The scene form of <see cref="WriteScene" />, as bytes.</summary>
    public static byte[] WriteSceneToBytes(
        string sceneName,
        IEnumerable<XnGineMeshInstance> instances,
        Func<int, int, XnGineTexturePng?>? textureProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sceneName);
        ArgumentNullException.ThrowIfNull(instances);

        using var stream = new MemoryStream();
        BuildScene(sceneName, instances, textureProvider).WriteGLB(stream);
        return stream.ToArray();
    }

    private static ModelRoot BuildScene(
        string sceneName,
        IEnumerable<XnGineMeshInstance> instances,
        Func<int, int, XnGineTexturePng?>? textureProvider)
    {
        // The meshes are built Y-flipped, like the single-mesh path, so a vertex reaching the scene
        // is already F*v. Placing it with F*M*F therefore lands it at F*(M*v) — the placement done
        // in the game's Y-down space, then flipped once for glTF. F is its own inverse, and the
        // conjugation keeps the determinant positive, so the reversed winding stays correct.
        var flip = Matrix4x4.CreateScale(1, -1, 1);
        var scene = new SceneBuilder(sceneName);
        var built =
            new Dictionary<XnGineTriangleMesh, MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>>();

        foreach (var instance in instances)
        {
            if (!built.TryGetValue(instance.Mesh, out var meshBuilder))
            {
                meshBuilder = BuildMesh(instance.Mesh, textureProvider);
                built[instance.Mesh] = meshBuilder;
            }

            scene.AddRigidMesh(meshBuilder, flip * instance.Transform * flip);
        }

        return scene.ToGltf2();
    }

    private static ModelRoot Build(XnGineTriangleMesh mesh, Func<int, int, XnGineTexturePng?>? textureProvider)
    {
        var scene = new SceneBuilder("mesh_" + mesh.ObjectId.ToString(CultureInfo.InvariantCulture));
        scene.AddRigidMesh(BuildMesh(mesh, textureProvider), Matrix4x4.Identity);
        return scene.ToGltf2();
    }

    private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> BuildMesh(
        XnGineTriangleMesh mesh,
        Func<int, int, XnGineTexturePng?>? textureProvider)
    {
        var meshName = "mesh_" + mesh.ObjectId.ToString(CultureInfo.InvariantCulture);
        var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(meshName);

        foreach (var subMesh in mesh.SubMeshes)
        {
            if (subMesh.TriangleCount == 0)
            {
                continue;
            }

            var texture = textureProvider?.Invoke(subMesh.TextureArchive, subMesh.TextureRecord);
            var material = CreateMaterial(subMesh, texture);
            var scaleU = 1f / (texture?.Width ?? DefaultTextureSize);
            var scaleV = 1f / (texture?.Height ?? DefaultTextureSize);
            var primitive = meshBuilder.UsePrimitive(material);

            for (var i = 0; i + 2 < subMesh.Indices.Count; i += 3)
            {
                // Reversed second/third so the winding survives the Y flip.
                primitive.AddTriangle(
                    Vertex(subMesh.Vertices[subMesh.Indices[i]], scaleU, scaleV),
                    Vertex(subMesh.Vertices[subMesh.Indices[i + 2]], scaleU, scaleV),
                    Vertex(subMesh.Vertices[subMesh.Indices[i + 1]], scaleU, scaleV));
            }
        }

        return meshBuilder;
    }

    private static MaterialBuilder CreateMaterial(XnGineSubMesh subMesh, XnGineTexturePng? texture)
    {
        var name = texture?.Name ?? string.Create(CultureInfo.InvariantCulture,
            $"TEXTURE.{subMesh.TextureArchive:D3}#{subMesh.TextureRecord}");
        var material = new MaterialBuilder(name)
            .WithDoubleSide(true)
            .WithMetallicRoughnessShader()
            .WithMetallicRoughness(0f, 1f);

        if (texture is not null)
        {
            material.WithBaseColor(ImageBuilder.From(new MemoryImage(texture.Png)));
        }

        return material;
    }

    private static (VertexPositionNormal, VertexTexture1) Vertex(XnGineVertex vertex, float scaleU, float scaleV)
    {
        var position = new Vector3(vertex.Position.X, -vertex.Position.Y, vertex.Position.Z);
        // A squared length is never negative, so "not positive" is exactly "no normal authored".
        var normal = new Vector3(vertex.Normal.X, -vertex.Normal.Y, vertex.Normal.Z);
        if (normal.LengthSquared() <= 0)
        {
            normal = Vector3.UnitY;
        }

        return (
            new VertexPositionNormal(position, normal),
            new VertexTexture1(new Vector2(vertex.TexelUv.X * scaleU, vertex.TexelUv.Y * scaleV)));
    }
}

using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

namespace BethesdaMultitool.Core.Formats.Xngine.Mesh;

/// <summary>
///     A material's texture as the viewer wants it: the lookup path its pixels are registered
///     under (a generated texture on the scene, or a path the texture sources resolve) and the
///     pixel size the mesh's texel UVs are divided by.
/// </summary>
internal sealed record XnGineViewerTexture(string Path, int Width, int Height);

/// <summary>
///     Adapts a decoded classic (XnGine) mesh into the <see cref="GlbScene" /> the native viewer
///     consumes, so Arena/Daggerfall/Battlespire/Redguard geometry can be inspected in the GUI and
///     not merely exported to a file.
///     <para>
///         ⚠ <b>This is NOT the same target space as the GLB file exporter.</b>
///         <see cref="XnGineMeshGlbExporter" /> writes glTF, which is Y-up, and gets there by
///         negating Y. The viewer is not glTF: its camera builds its view matrix with
///         <c>Vector3.UnitZ</c> as the up vector, i.e. the Z-up NIF basis, and
///         <c>GltfCoordinateAdapter</c> applies the Y-up rotation only when a NIF scene is written
///         out. Feeding this adapter's output through the exporter's flip, or vice versa, lays
///         every classic mesh on its side.
///     </para>
///     <para>
///         The mapping, derived rather than guessed: classic geometry is <b>Y-down</b> (that is why
///         the exporter negates Y for glTF and reverses winding to compensate). The viewer wants
///         X right, Y forward, Z up. So <c>(x, y, z)</c> becomes <c>(x, z, -y)</c> — a -90° rotation
///         about X. Its determinant is +1, a proper rotation, so
///         <b>
///             winding is preserved and the
///             triangle indices are NOT reversed
///         </b>
///         ; that is the difference from the exporter's path,
///         where a mirror forces the reversal.
///     </para>
/// </summary>
internal static class XnGineViewerSceneAdapter
{
    /// <summary>
    ///     Largest vertex count a single sub-mesh may contribute.
    ///     <see cref="RenderableSubmesh.Triangles" /> is <c>ushort[]</c>, so an index above this
    ///     cannot be represented. Classic meshes are far below it — the largest Daggerfall record is
    ///     in the low thousands of points — but a malformed archive must fail loudly rather than
    ///     silently wrap an index and draw garbage.
    /// </summary>
    public const int MaximumVerticesPerSubMesh = ushort.MaxValue;

    /// <summary>Converts a classic mesh into a viewer scene, one mesh part per textured sub-mesh.</summary>
    /// <param name="mesh">The decomposed triangle mesh.</param>
    /// <param name="sceneName">Node name for the mesh root.</param>
    /// <param name="texturePathFor">
    ///     Optional resolver from (archive, record) to a diffuse texture path. Classic meshes carry
    ///     texture INDICES, not paths, so without this the scene renders untextured — which is the
    ///     honest result for a game whose textures are not decoded yet.
    /// </param>
    public static GlbScene ToViewerScene(
        XnGineTriangleMesh mesh, string sceneName, Func<int, int, string?>? texturePathFor = null)
    {
        return ToViewerScene(mesh, sceneName, PathOnly(texturePathFor));
    }

    /// <summary>
    ///     The textured form of the single-mesh conversion: the resolver also states each texture's
    ///     pixel size, and the mesh's texel UVs are divided by it so the viewer samples the image
    ///     the way the GLB exporter does. ⚠ Without the size the UVs stay in texels, which is right
    ///     for an untextured scene and wrong for a textured one.
    /// </summary>
    public static GlbScene ToViewerScene(
        XnGineTriangleMesh mesh, string sceneName, Func<int, int, XnGineViewerTexture?>? textureFor)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(sceneName);

        // GlbScene's constructor already owns index 0 as "SceneRoot"; the mesh hangs UNDER it.
        // Adding another parentless root here would leave the scene with two, which is not what any
        // consumer walking from RootNodeIndex expects.
        var scene = new GlbScene();
        var rootIndex = scene.AddNode(
            sceneName, GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, sceneName);

        for (var i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var subMesh = mesh.SubMeshes[i];
            if (subMesh.Vertices.Count == 0 || subMesh.Indices.Count < 3)
            {
                // Empty sub-meshes are normal in these archives (placeholder records), not an error.
                continue;
            }

            if (subMesh.Vertices.Count > MaximumVerticesPerSubMesh)
            {
                throw new InvalidDataException(
                    $"Sub-mesh {i} of mesh {mesh.ObjectId} has {subMesh.Vertices.Count} vertices, " +
                    $"above the {MaximumVerticesPerSubMesh} a 16-bit index can address.");
            }

            scene.MeshParts.Add(new GlbMeshPart
            {
                Name = $"{sceneName}_sub{i:D2}",
                NodeIndex = rootIndex,
                Submesh = BuildSubmesh(subMesh, textureFor)
            });
        }

        return scene;
    }

    /// <summary>
    ///     Assembles a whole level: many placed instances of (possibly shared) meshes.
    ///     <para>
    ///         ⚠ The placement transform arrives in the meshes' OWN Y-down space, while the vertices
    ///         this adapter emits are already rotated into the viewer's Z-up basis. Applying M
    ///         directly would rotate an already-rotated vertex. The transform is therefore
    ///         CONJUGATED. ⚠ <c>System.Numerics</c> is ROW-vector (<c>v * M</c>), so the order is
    ///         <c>R⁻¹ * M * R</c> and NOT the <c>R * M * R⁻¹</c> of column-vector textbooks —
    ///         writing the textbook form compiles, runs, and places every object wrongly. It is
    ///         chosen so that a viewer-space vertex <c>v*R</c> lands at <c>(v*M)*R</c>: the
    ///         placement done in the game's space, converted once. The GLB exporter does the same
    ///         with its mirror (<c>F*M*F</c>), where F being its own inverse hides the ordering
    ///         question; here R is a rotation, so the inverse is a genuine inverse and the order is
    ///         visible.
    ///     </para>
    /// </summary>
    public static GlbScene ToViewerScene(
        string sceneName, IEnumerable<XnGineMeshInstance> instances, Func<int, int, string?>? texturePathFor = null)
    {
        return ToViewerScene(sceneName, instances, PathOnly(texturePathFor));
    }

    /// <summary>The textured form of the level assembly; see the single-mesh overload for the UV rule.</summary>
    public static GlbScene ToViewerScene(
        string sceneName, IEnumerable<XnGineMeshInstance> instances, Func<int, int, XnGineViewerTexture?>? textureFor)
    {
        ArgumentNullException.ThrowIfNull(sceneName);
        ArgumentNullException.ThrowIfNull(instances);

        var toViewer = Matrix4x4.CreateRotationX(-MathF.PI / 2);
        var fromViewer = Matrix4x4.CreateRotationX(MathF.PI / 2);

        var scene = new GlbScene();
        var placed = 0;
        foreach (var instance in instances)
        {
            var local = fromViewer * instance.Transform * toViewer;
            var nodeIndex = scene.AddNode(
                instance.Name, GlbScene.RootNodeIndex, local, local, GlbNodeKind.Attachment);

            for (var i = 0; i < instance.Mesh.SubMeshes.Count; i++)
            {
                var subMesh = instance.Mesh.SubMeshes[i];
                if (subMesh.Vertices.Count is 0 or > MaximumVerticesPerSubMesh || subMesh.Indices.Count < 3)
                {
                    continue;
                }

                scene.MeshParts.Add(new GlbMeshPart
                {
                    Name = $"{instance.Name}_sub{i:D2}",
                    NodeIndex = nodeIndex,
                    Submesh = BuildSubmesh(subMesh, textureFor)
                });
            }

            placed++;
        }

        if (placed == 0)
        {
            throw new InvalidDataException($"Level '{sceneName}' placed no meshes.");
        }

        return scene;
    }

    /// <summary>Adapts a path-only resolver: no size, so UVs stay in texel units.</summary>
    private static Func<int, int, XnGineViewerTexture?>? PathOnly(Func<int, int, string?>? texturePathFor)
    {
        if (texturePathFor is null)
        {
            return null;
        }

        return (archive, record) =>
        {
            var path = texturePathFor(archive, record);
            return path is null ? null : new XnGineViewerTexture(path, 0, 0);
        };
    }

    /// <summary>Rotates a classic Y-down vector into the viewer's Z-up basis.</summary>
    public static Vector3 ToViewerSpace(Vector3 classic)
    {
        return new Vector3(classic.X, classic.Z, -classic.Y);
    }

    private static RenderableSubmesh BuildSubmesh(
        XnGineSubMesh subMesh, Func<int, int, XnGineViewerTexture?>? textureFor)
    {
        var texture = textureFor?.Invoke(subMesh.TextureArchive, subMesh.TextureRecord);
        var scaleU = texture is { Width: > 0 } ? 1f / texture.Width : 1f;
        var scaleV = texture is { Height: > 0 } ? 1f / texture.Height : 1f;

        var count = subMesh.Vertices.Count;
        var positions = new float[count * 3];
        var normals = new float[count * 3];
        var uvs = new float[count * 2];

        for (var v = 0; v < count; v++)
        {
            var vertex = subMesh.Vertices[v];
            var position = ToViewerSpace(vertex.Position);
            var normal = ToViewerSpace(vertex.Normal);

            positions[v * 3] = position.X;
            positions[v * 3 + 1] = position.Y;
            positions[v * 3 + 2] = position.Z;

            normals[v * 3] = normal.X;
            normals[v * 3 + 1] = normal.Y;
            normals[v * 3 + 2] = normal.Z;

            uvs[v * 2] = vertex.TexelUv.X * scaleU;
            uvs[v * 2 + 1] = vertex.TexelUv.Y * scaleV;
        }

        // Winding is carried through unchanged: the basis change is a rotation, not a mirror.
        var triangles = new ushort[subMesh.Indices.Count];
        for (var i = 0; i < subMesh.Indices.Count; i++)
        {
            triangles[i] = (ushort)subMesh.Indices[i];
        }

        return new RenderableSubmesh
        {
            Positions = positions,
            Triangles = triangles,
            Normals = normals,
            UVs = uvs,
            ShapeName = $"archive{subMesh.TextureArchive}_record{subMesh.TextureRecord}",
            DiffuseTexturePath = texture?.Path
        };
    }
}

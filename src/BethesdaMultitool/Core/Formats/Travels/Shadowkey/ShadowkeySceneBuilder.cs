using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     Which way up a Shadowkey mesh should be placed in the viewer.
/// </summary>
internal enum ShadowkeyAxisConvention
{
    /// <summary>
    ///     Keep the file's own axes. The SECOND component is up in the source data (measured: the
    ///     pine tree is 1,897 units tall in component 1, a humanoid tunic 657), so a mesh loaded
    ///     this way lies on its side in a Z-up viewer. Useful for inspecting raw values.
    /// </summary>
    Source,

    /// <summary>
    ///     Rotate the source Y-up data into the Z-up convention the Bethesda viewer uses, by
    ///     mapping <c>(x, y, z)</c> to <c>(x, -z, y)</c>: a proper rotation (determinant +1), the
    ///     mesh-to-zone map the cut-2 placement measurement selected (mesh +Y to zone +Z, mesh +Z to
    ///     zone -Y; 7,687 blocked vertices against 21,017 for the old <c>(x, z, y)</c> reflection,
    ///     <c>ShadowkeyModelUnits.MeshToZone</c>). The x mirror is not measured; the proper map
    ///     keeps every mesh congruent to its own document.
    /// </summary>
    ZUp
}

/// <summary>
///     Bridges a decoded <see cref="ShadowkeyMesh" /> into the renderer-neutral
///     <see cref="BethesdaViewerScene" /> the existing D3D12 viewer consumes.
///     <para>
///         This is an ADAPTER, not a decoder — every value it reads is already parsed. Two things
///         it has to reconcile:
///     </para>
///     <list type="bullet">
///         <item>
///             UVs arrive in TEXELS and are normalised here by the skin's own dimensions. Values
///             outside 0..1 are normal and are left alone for the sampler to wrap.
///         </item>
///         <item>
///             Faces carry no texture index, so a mesh is ONE submesh. Extra textures in the
///             record are alternative whole-mesh skins (male_long_tunic.bin ships 19 recolours),
///             and which one an actor uses is decided by the entity table, not the mesh — so the
///             skin is a caller's choice rather than something to infer here.
///         </item>
///     </list>
/// </summary>
internal static class ShadowkeySceneBuilder
{
    /// <summary>
    ///     The scene texture key a built mesh uses for its skin. Generated textures have no
    ///     archive behind them, so the key only has to be stable and unique within the scene.
    /// </summary>
    internal static string SkinTextureKey(string meshName, int skin)
    {
        return $"shadowkey:{meshName}#skin{skin}";
    }

    /// <summary>
    ///     Builds a single-mesh scene showing <paramref name="frame" /> of <paramref name="mesh" />
    ///     with skin <paramref name="skin" /> applied.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="frame" /> or <paramref name="skin" /> is outside the record.
    /// </exception>
    /// <exception cref="NotSupportedException">
    ///     The mesh has more corners than a 16-bit index buffer can address. No retail record does
    ///     (the largest is far below the limit), but the format allows 65,535 faces and silently
    ///     truncating them would draw a corrupt mesh rather than report the problem.
    /// </exception>
    public static BethesdaViewerScene BuildMeshScene(
        ShadowkeyMesh mesh,
        int frame = 0,
        int skin = 0,
        ShadowkeyAxisConvention axes = ShadowkeyAxisConvention.ZUp,
        bool magentaIsTransparent = true)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var submesh = BuildSubmesh(mesh, frame, skin, axes, magentaIsTransparent, out var texture);

        var scene = new BethesdaViewerScene(
            mesh.Name,
            BethesdaViewerScenePurpose.ClassicMesh,
            ComputeBounds(submesh.Positions),
            BethesdaGame.Shadowkey);

        scene.AddGeneratedTexture(submesh.DiffuseTexturePath!, texture);
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = mesh.Name,
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = submesh
        });

        return scene;
    }

    /// <summary>
    ///     Converts one frame of <paramref name="mesh" /> into a renderable submesh, and hands
    ///     back the decoded skin so the caller can register it on whichever scene it is building.
    /// </summary>
    public static RenderableSubmesh BuildSubmesh(
        ShadowkeyMesh mesh,
        int frame,
        int skin,
        ShadowkeyAxisConvention axes,
        bool magentaIsTransparent,
        out DecodedTexture skinTexture)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var triangles = mesh.ToUnrolledTriangles(frame);
        var cornerCount = triangles.Positions.Length;
        if (cornerCount > ushort.MaxValue + 1)
        {
            throw new NotSupportedException(
                $"'{mesh.Name}' unrolls to {cornerCount} corners, more than a 16-bit index buffer " +
                "can address.");
        }

        var image = mesh.DecodeSkin(skin, magentaIsTransparent);
        skinTexture = image.ToDecodedTexture();

        var positions = new float[cornerCount * 3];
        for (var i = 0; i < cornerCount; i++)
        {
            var p = Orient(triangles.Positions[i], axes);
            positions[i * 3 + 0] = p.X;
            positions[i * 3 + 1] = p.Y;
            positions[i * 3 + 2] = p.Z;
        }

        // Texel UVs normalise against the SKIN's dimensions, which is why this needs the texture
        // set rather than a constant. Out-of-range results are expected and are left to wrap.
        var uvScaleU = mesh.Textures.Width > 0 ? 1f / mesh.Textures.Width : 1f;
        var uvScaleV = mesh.Textures.Height > 0 ? 1f / mesh.Textures.Height : 1f;
        var uvs = new float[cornerCount * 2];
        for (var i = 0; i < cornerCount; i++)
        {
            uvs[i * 2 + 0] = triangles.TexelUvs[i].X * uvScaleU;
            uvs[i * 2 + 1] = triangles.TexelUvs[i].Y * uvScaleV;
        }

        // ToUnrolledTriangles emits one index per corner in order, so the index buffer is the
        // identity. Building it here rather than casting keeps the ushort range check meaningful.
        var indices = new ushort[cornerCount];
        for (var i = 0; i < cornerCount; i++)
        {
            indices[i] = (ushort)i;
        }

        return new RenderableSubmesh
        {
            ShapeName = mesh.Name,
            Positions = positions,
            Triangles = indices,
            UVs = uvs,
            DiffuseTexturePath = SkinTextureKey(mesh.Name, skin)
        };
    }

    /// <summary>Applies the axis convention to one source position.</summary>
    private static Vector3 Orient(Vector3 source, ShadowkeyAxisConvention axes)
    {
        return axes == ShadowkeyAxisConvention.ZUp
            ? new Vector3(source.X, -source.Z, source.Y)
            : source;
    }

    /// <summary>Axis-aligned bounds over a flattened xyz position array.</summary>
    private static BethesdaViewerBounds? ComputeBounds(float[] positions)
    {
        if (positions.Length < 3)
        {
            return null;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new BethesdaViewerBounds(min, max);
    }
}

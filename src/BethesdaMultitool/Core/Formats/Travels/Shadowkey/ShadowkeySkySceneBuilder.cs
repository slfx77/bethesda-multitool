using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     Bridges a decoded <see cref="ShadowkeySkybox" /> into a viewer scene as an ordinary mesh
///     part.
///     <para>
///         Shadowkey needs no dedicated sky pass: the <c>.zsk</c> already carries BOTH a shell mesh
///         and the 512x256 image painted on it, so the sky is geometry like everything else. That
///         is why a Shadowkey scene is <see cref="BethesdaViewerScenePurpose.ClassicMesh" /> and
///         never <c>RawNif</c> — the latter opts a scene into the native Gamebryo sky pass, which
///         would draw a second, wrong sky over this one.
///     </para>
///     <para>
///         <b>⚑ Three readings settled by measurement 2026-09-05, none of them stated by the file.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>+y is up, matching the models.</b> The shell's vertices are mostly NEGATIVE in y
///             (the outdoor dome spans -290..+40), which reads at a glance as a y-down convention
///             and would hang the sky under the world. It is not. The texture is a POLAR
///             projection with the zenith at its centre, so the zenith is whichever pole maps
///             there — and the single vertex at y=+40 maps to (0.496, 0.504), dead centre, while
///             the pole at y=-290 maps to the texture edge. The widest ring, radius 256 at y=-213,
///             maps to the full 0..1 rim and is therefore the HORIZON. So the shell is a
///             hemisphere — horizon radius 256, zenith 253 above it — plus a small nadir cap at
///             y=-290 that samples rim colour to close the bottom. The models agree independently:
///             pinetree.bin runs 0..1897 in y and witchtree.bin 0..2626, both rooted at zero and
///             growing positive.
///         </item>
///         <item>
///             <b>Corner UVs are NORMALISED, not texels.</b> They are u16 whose low byte is always
///             zero, so they carry 8 bits of value, and <see cref="ShadowkeySkyCorner" /> describes
///             them as 8.8 fixed point — which yields 0..255. That is neither: v would then span
///             exactly the 256-pixel height, but u would address only half of the 512-pixel width.
///             The image settles it — in all eight painted skies the right half holds content that
///             DIFFERS from the left, so the full width is real — and dividing by 65,536 instead
///             gives 0..0.996 on both axes, covering the whole image either way. Hence
///             <see cref="UvScale" />, and hence this builder does not use
///             <see cref="ShadowkeySkyCorner.UnitsU" />.
///         </item>
///         <item>
///             <b>The shell's size has NO established relation to the cell grid</b> — it is
///             authored at a fixed radius for a camera-relative pass, so 256 units is not some
///             number of tiles. Placing it in world space is therefore a DISPLAY choice, and this
///             builder makes it explicitly: the shell is scaled to enclose the zone and centred on
///             it. Nothing is claimed about what the engine did.
///         </item>
///     </list>
/// </summary>
internal static class ShadowkeySkySceneBuilder
{
    /// <summary>The scene texture key a built sky uses for its image.</summary>
    internal static string SkyTextureKey(string zoneName) => $"shadowkey:{zoneName}#sky";

    /// <summary>
    ///     Divisor turning a raw corner coordinate into a normalised 0..1 UV. See reading 2 in the
    ///     type remarks: the field is a u16 fraction, not the 8.8 texel count it looks like.
    /// </summary>
    public const float UvScale = 65_536f;

    /// <summary>
    ///     How much larger than the zone's own diagonal the shell is drawn, so the horizon sits
    ///     outside the geometry rather than cutting through it. A display constant.
    /// </summary>
    public const float EnclosureMargin = 1.15f;

    /// <summary>
    ///     Adds <paramref name="skybox" /> to <paramref name="scene" />, sized to enclose a zone
    ///     <paramref name="widthTiles" /> by <paramref name="heightTiles" /> and centred on it.
    /// </summary>
    /// <param name="scene">The scene to add to.</param>
    /// <param name="skybox">The parsed <c>.zsk</c>.</param>
    /// <param name="palette">The zone's <c>.pal</c>, through which the sky image is decoded.</param>
    /// <param name="zoneName">Names the mesh part and keys its texture.</param>
    /// <param name="widthTiles">Zone width, for sizing.</param>
    /// <param name="heightTiles">Zone height, for sizing.</param>
    /// <param name="baseHeightTiles">
    ///     Where the shell's own horizon ring sits in z. Defaults to 0, the zone's nominal ground.
    /// </param>
    /// <returns>The mesh part that was added.</returns>
    public static BethesdaViewerMeshPart Add(
        BethesdaViewerScene scene,
        ShadowkeySkybox skybox,
        Palette palette,
        string zoneName,
        float widthTiles,
        float heightTiles,
        float baseHeightTiles = 0f)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(skybox);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneName);

        var key = SkyTextureKey(zoneName);
        scene.AddGeneratedTexture(key, skybox.Texture.ToDecodedTexture(palette));

        var submesh = BuildSubmesh(skybox, key, widthTiles, heightTiles, baseHeightTiles);
        var part = new BethesdaViewerMeshPart
        {
            Name = $"{zoneName}#sky",
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = submesh
        };

        scene.MeshParts.Add(part);
        return part;
    }

    /// <summary>
    ///     Unrolls the shell into a submesh. Faces index vertices and UV corners independently, as
    ///     in <see cref="ShadowkeyMesh" />, so each face contributes three fresh corners.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     A face indexes a vertex or corner the record does not hold.
    /// </exception>
    /// <exception cref="NotSupportedException">
    ///     The shell has more corners than a 16-bit index buffer can address. No retail file comes
    ///     close (the largest is 192 faces), but the format permits it.
    /// </exception>
    public static RenderableSubmesh BuildSubmesh(
        ShadowkeySkybox skybox,
        string textureKey,
        float widthTiles,
        float heightTiles,
        float baseHeightTiles = 0f)
    {
        ArgumentNullException.ThrowIfNull(skybox);

        var cornerCount = skybox.Faces.Count * 3;
        if (cornerCount > ushort.MaxValue + 1)
        {
            throw new NotSupportedException(
                $"'{skybox.Name}' unrolls to {cornerCount} corners, more than a 16-bit index " +
                "buffer can address.");
        }

        var scale = EnclosureScale(skybox, widthTiles, heightTiles);
        var centre = new Vector3(widthTiles * 0.5f, heightTiles * 0.5f, baseHeightTiles);
        var horizon = HorizonHeight(skybox);

        var positions = new float[cornerCount * 3];
        var uvs = new float[cornerCount * 2];
        var indices = new ushort[cornerCount];

        for (var f = 0; f < skybox.Faces.Count; f++)
        {
            var face = skybox.Faces[f];
            Span<int> vertexIndices = [face.V0, face.V1, face.V2];
            Span<int> cornerIndices = [face.C0, face.C1, face.C2];

            for (var k = 0; k < 3; k++)
            {
                var v = Require(vertexIndices[k], skybox.Vertices.Count, skybox.Name, "vertex");
                var c = Require(cornerIndices[k], skybox.Corners.Count, skybox.Name, "corner");
                var corner = (f * 3) + k;

                var source = skybox.Vertices[v];

                // Source is y-up (reading 1); the viewer is z-up, so (x, y, z) becomes (x, z, y),
                // the same swap the mesh bridge makes. The horizon ring is pulled to
                // baseHeightTiles so the sky meets the ground rather than floating.
                var placed = new Vector3(source.X, source.Z, source.Y - horizon) * scale;
                positions[(corner * 3) + 0] = placed.X + centre.X;
                positions[(corner * 3) + 1] = placed.Y + centre.Y;
                positions[(corner * 3) + 2] = placed.Z + centre.Z;

                uvs[(corner * 2) + 0] = skybox.Corners[c].U / UvScale;
                uvs[(corner * 2) + 1] = skybox.Corners[c].V / UvScale;
                indices[corner] = (ushort)corner;
            }
        }

        return new RenderableSubmesh
        {
            ShapeName = $"{skybox.Name}#sky",
            Positions = positions,
            Triangles = indices,
            UVs = uvs,
            DiffuseTexturePath = textureKey
        };
    }

    /// <summary>
    ///     The shell's own horizon height: the y of its widest ring. Subtracting it puts the
    ///     horizon at the caller's ground level instead of wherever the author centred the shell.
    /// </summary>
    public static float HorizonHeight(ShadowkeySkybox skybox)
    {
        ArgumentNullException.ThrowIfNull(skybox);

        var widest = -1f;
        var height = 0f;
        foreach (var vertex in skybox.Vertices)
        {
            var radius = MathF.Sqrt((vertex.X * (float)vertex.X) + (vertex.Z * (float)vertex.Z));
            if (radius > widest)
            {
                widest = radius;
                height = vertex.Y;
            }
        }

        return height;
    }

    /// <summary>
    ///     Scale that puts the shell's horizon ring outside the zone's diagonal. Returns 1 for a
    ///     degenerate shell rather than dividing by zero.
    /// </summary>
    public static float EnclosureScale(ShadowkeySkybox skybox, float widthTiles, float heightTiles)
    {
        ArgumentNullException.ThrowIfNull(skybox);

        var widest = 0f;
        foreach (var vertex in skybox.Vertices)
        {
            widest = MathF.Max(
                widest, MathF.Sqrt((vertex.X * (float)vertex.X) + (vertex.Z * (float)vertex.Z)));
        }

        if (widest <= 0f)
        {
            return 1f;
        }

        var half = MathF.Sqrt((widthTiles * widthTiles) + (heightTiles * heightTiles)) * 0.5f;
        return half * EnclosureMargin / widest;
    }

    private static int Require(int index, int count, string name, string what)
    {
        if ((uint)index >= (uint)count)
        {
            throw new InvalidDataException(
                $"'{name}': a face indexes {what} {index}, but the record holds {count}.");
        }

        return index;
    }
}

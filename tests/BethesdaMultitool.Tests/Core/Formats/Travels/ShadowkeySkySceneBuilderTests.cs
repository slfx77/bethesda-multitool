using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for <see cref="ShadowkeySkySceneBuilder" />, the <c>.zsk</c>-to-scene
///     bridge.
///     <para>
///         Two readings carry this bridge and neither is stated by the file: the shell is y-UP
///         despite most of its vertices being negative, and its corner UVs are NORMALISED rather
///         than the 8.8 texels the record looks like. Both were settled against retail bytes and
///         both are re-derived from those bytes in
///         <see cref="ShadowkeySkySceneRetailTests" />; these tests pin what the builder does with
///         them. A wrong call on either draws a sky that renders without complaint — inside out,
///         or sampling a quarter of the image — so neither can be left to a comment.
///     </para>
/// </summary>
public sealed class ShadowkeySkySceneBuilderTests
{
    /// <summary>Vertices of the synthetic shell: apex, a four-point horizon ring, and a nadir.</summary>
    private static readonly (short X, short Y, short Z)[] ShellVertices =
    [
        (0, 100, 0),        // 0: zenith, radius 0
        (200, -50, 0),      // 1..4: horizon ring, radius 200 at y = -50
        (0, -50, 200),
        (-200, -50, 0),
        (0, -50, -200),
        (0, -150, 0)        // 5: nadir cap, radius 0
    ];

    /// <summary>
    ///     Corner UVs. 32,768 is exactly half of 65,536, so a correct normalisation reads 0.5 and
    ///     the rival 8.8 reading would read 128.
    /// </summary>
    private static readonly (ushort U, ushort V)[] ShellCorners =
    [
        (32768, 32768),
        (65280, 0),
        (0, 65280)
    ];

    /// <summary>Two faces over the shell, each taking all three corners.</summary>
    private static readonly (ushort V0, ushort V1, ushort V2)[] ShellFaces = [(0, 1, 2), (0, 2, 3)];

    private static ShadowkeySkybox Shell(int gapLength = ShadowkeySkybox.OutdoorGapLength)
    {
        var meshLength = ShadowkeySkybox.HeaderLength + (ShellVertices.Length * 6) +
                         (ShellCorners.Length * 4) + (ShellFaces.Length * 12);
        var payload = new byte[
            meshLength + gapLength + ShadowkeySkybox.TextureLength + ShadowkeySkybox.FooterLength];

        BinaryPrimitives.WriteUInt16LittleEndian(payload, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)ShellVertices.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), (ushort)ShellCorners.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), (ushort)ShellFaces.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), (ushort)(ShellVertices.Length * 3));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);

        for (var i = 0; i < ShellVertices.Length; i++)
        {
            var offset = ShadowkeySkybox.HeaderLength + (i * 6);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset), ShellVertices[i].X);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset + 2), ShellVertices[i].Y);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset + 4), ShellVertices[i].Z);
        }

        var cornerOffset = ShadowkeySkybox.HeaderLength + (ShellVertices.Length * 6);
        for (var i = 0; i < ShellCorners.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(cornerOffset + (i * 4)), ShellCorners[i].U);
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(cornerOffset + (i * 4) + 2), ShellCorners[i].V);
        }

        var faceOffset = cornerOffset + (ShellCorners.Length * 4);
        for (var i = 0; i < ShellFaces.Length; i++)
        {
            var offset = faceOffset + (i * 12);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), ShellFaces[i].V0);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 2), ShellFaces[i].V1);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 4), ShellFaces[i].V2);
            for (var word = 0; word < 3; word++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    payload.AsSpan(offset + 6 + (word * 2)), (ushort)word);
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 2), 10);
        return ShadowkeySkybox.Parse(payload, "testzone.zsk");
    }

    private static Palette GreyPalette()
    {
        var rgb = new byte[Palette.RgbByteCount];
        for (var i = 0; i < Palette.EntryCount; i++)
        {
            rgb[i * 3] = (byte)i;
            rgb[(i * 3) + 1] = (byte)i;
            rgb[(i * 3) + 2] = (byte)i;
        }

        return Palette.FromRgb8(rgb);
    }

    private static BethesdaViewerScene EmptyScene() =>
        new("testzone", BethesdaViewerScenePurpose.ClassicMesh);

    /// <summary>Vertex <paramref name="index" /> of a submesh, as a point.</summary>
    private static (float X, float Y, float Z) Vertex(float[] positions, int index) =>
        (positions[index * 3], positions[(index * 3) + 1], positions[(index * 3) + 2]);

    // ---------------------------------------------------------------- the two settled readings

    /// <summary>
    ///     ⚑ UVs are normalised. 32,768 is half of 65,536, so the centre corner must read 0.5 —
    ///     the rival 8.8 reading (which <see cref="ShadowkeySkyCorner.UnitsU" /> still exposes)
    ///     would read 128 and wrap the texture 128 times.
    /// </summary>
    [Fact]
    public void CornerUvs_AreNormalisedNotTexels()
    {
        var submesh = ShadowkeySkySceneBuilder.BuildSubmesh(Shell(), "sky", 100, 100);

        Assert.Equal(0.5f, submesh.UVs![0], 4);
        Assert.Equal(0.5f, submesh.UVs[1], 4);

        // Every UV the shell carries must land inside the unit square.
        Assert.All(submesh.UVs, uv => Assert.InRange(uv, 0f, 1f));
    }

    /// <summary>
    ///     ⚑ The shell is y-up, so source y becomes scene z and the zenith ends up ABOVE the
    ///     horizon ring. Read the other way the sky renders inside out, under the world.
    /// </summary>
    [Fact]
    public void TheZenithSitsAboveTheHorizonRing()
    {
        var submesh = ShadowkeySkySceneBuilder.BuildSubmesh(Shell(), "sky", 100, 100);

        // Face 0 is (zenith, ring, ring), so corner 0 is the zenith and corners 1-2 are the ring.
        var zenith = Vertex(submesh.Positions, 0);
        var ring = Vertex(submesh.Positions, 1);

        Assert.True(zenith.Z > ring.Z, $"Zenith z {zenith.Z} is not above the horizon ring z {ring.Z}.");
    }

    /// <summary>
    ///     Source x and z are the horizontal axes and reach the scene unchanged in orientation;
    ///     only the vertical axis moves. Checked on the ring vertex whose source z is non-zero, so
    ///     a swap of the two horizontal axes cannot pass.
    /// </summary>
    [Fact]
    public void SourceXAndZStayHorizontal()
    {
        var submesh = ShadowkeySkySceneBuilder.BuildSubmesh(Shell(), "sky", 100, 100);
        var scale = ShadowkeySkySceneBuilder.EnclosureScale(Shell(), 100, 100);

        // Corner 2 of face 0 is vertex 2, source (0, -50, 200): purely +z horizontally.
        var point = Vertex(submesh.Positions, 2);
        Assert.Equal(50f, point.X, 3);                       // centred: 100/2 + 0
        Assert.Equal(50f + (200 * scale), point.Y, 3);       // source z became scene y
        Assert.Equal(0f, point.Z, 3);                        // on the horizon plane
    }

    // ---------------------------------------------------------------- sizing

    /// <summary>The horizon is the widest ring, not the lowest or the first vertex.</summary>
    [Fact]
    public void HorizonHeight_IsTheWidestRing()
    {
        Assert.Equal(-50f, ShadowkeySkySceneBuilder.HorizonHeight(Shell()));
    }

    /// <summary>
    ///     The shell is scaled to enclose the zone's diagonal with a margin, because its authored
    ///     radius has no known relation to the cell grid. A zone that poked through its own sky
    ///     would look like a decode fault rather than the display choice it is.
    /// </summary>
    [Fact]
    public void EnclosureScale_PutsTheHorizonOutsideTheZoneDiagonal()
    {
        var scale = ShadowkeySkySceneBuilder.EnclosureScale(Shell(), 100, 100);

        var halfDiagonal = MathF.Sqrt((100f * 100f) + (100f * 100f)) * 0.5f;
        Assert.Equal(halfDiagonal * ShadowkeySkySceneBuilder.EnclosureMargin / 200f, scale, 5);
        Assert.True(200f * scale > halfDiagonal, "The horizon must fall outside the zone diagonal.");
    }

    /// <summary>The horizon ring lands at the ground height the caller names.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-3.5f)]
    public void TheHorizonRingLandsAtTheCallersGroundHeight(float ground)
    {
        var submesh = ShadowkeySkySceneBuilder.BuildSubmesh(Shell(), "sky", 100, 100, ground);

        Assert.Equal(ground, Vertex(submesh.Positions, 1).Z, 3);
    }

    /// <summary>The shell is centred on the zone rather than sitting at the origin corner.</summary>
    [Fact]
    public void TheShellIsCentredOnTheZone()
    {
        var submesh = ShadowkeySkySceneBuilder.BuildSubmesh(Shell(), "sky", 128, 64);

        Assert.Equal(64f, Vertex(submesh.Positions, 0).X, 3);
        Assert.Equal(32f, Vertex(submesh.Positions, 0).Y, 3);
    }

    // ---------------------------------------------------------------- scene wiring

    /// <summary>
    ///     The sky arrives as an ordinary mesh part with its image registered as a generated
    ///     texture — no sky pass, no archive behind it.
    /// </summary>
    [Fact]
    public void Add_RegistersTheImageAndAddsOneMeshPart()
    {
        var scene = EmptyScene();

        var part = ShadowkeySkySceneBuilder.Add(scene, Shell(), GreyPalette(), "azra", 128, 128);

        Assert.Same(part, Assert.Single(scene.MeshParts));
        Assert.Equal(ShadowkeySkySceneBuilder.SkyTextureKey("azra"), part.Submesh.DiffuseTexturePath);
        Assert.True(scene.TryGetGeneratedTexture(part.Submesh.DiffuseTexturePath!, out var texture));
        Assert.Equal(ShadowkeySkybox.TextureWidth, texture!.Width);
        Assert.Equal(ShadowkeySkybox.TextureHeight, texture.Height);
    }

    /// <summary>Three corners per face, and the index buffer addresses them all.</summary>
    [Fact]
    public void EveryFaceContributesThreeCorners()
    {
        var submesh = ShadowkeySkySceneBuilder.BuildSubmesh(Shell(), "sky", 100, 100);

        Assert.Equal(ShellFaces.Length * 3, submesh.Triangles.Length);
        Assert.Equal(ShellFaces.Length * 3 * 3, submesh.Positions.Length);
        Assert.Equal(ShellFaces.Length * 3 * 2, submesh.UVs!.Length);
        Assert.Equal(Enumerable.Range(0, ShellFaces.Length * 3).Select(i => (ushort)i), submesh.Triangles);
    }

    [Fact]
    public void Add_RejectsNullInputs()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShadowkeySkySceneBuilder.Add(null!, Shell(), GreyPalette(), "azra", 8, 8));
        Assert.Throws<ArgumentNullException>(
            () => ShadowkeySkySceneBuilder.Add(EmptyScene(), null!, GreyPalette(), "azra", 8, 8));
        Assert.Throws<ArgumentNullException>(
            () => ShadowkeySkySceneBuilder.Add(EmptyScene(), Shell(), null!, "azra", 8, 8));
    }

    /// <summary>A degenerate shell scales by one rather than dividing by zero.</summary>
    [Fact]
    public void AShellWithNoHorizontalExtent_ScalesByOne()
    {
        var flat = ShadowkeySkybox.Parse(FlatShellPayload(), "flat.zsk");

        Assert.Equal(1f, ShadowkeySkySceneBuilder.EnclosureScale(flat, 128, 128));
    }

    /// <summary>A shell whose vertices are all on the vertical axis, so every radius is zero.</summary>
    private static byte[] FlatShellPayload()
    {
        const int vertices = 3;
        const int corners = 3;
        const int faces = 1;
        var meshLength = ShadowkeySkybox.HeaderLength + (vertices * 6) + (corners * 4) + (faces * 12);
        var payload = new byte[
            meshLength + ShadowkeySkybox.OutdoorGapLength + ShadowkeySkybox.TextureLength +
            ShadowkeySkybox.FooterLength];

        BinaryPrimitives.WriteUInt16LittleEndian(payload, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), vertices);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), corners);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), faces);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), vertices * 3);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);

        for (var i = 0; i < vertices; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                payload.AsSpan(ShadowkeySkybox.HeaderLength + (i * 6) + 2), (short)(i * 10));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 2), 10);
        return payload;
    }
}

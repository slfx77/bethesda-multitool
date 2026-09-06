using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     The adapter that turns a decoded Shadowkey mesh into a viewer scene. Every expected value
///     here is derived from the synthetic record built below, whose bytes are written by hand, so
///     nothing is asserted against the production decoder's own opinion.
/// </summary>
public class ShadowkeySceneBuilderTests
{
    private const int TextureSize = 2;

    /// <summary>
    ///     A minimal but valid record: <paramref name="frames" /> frames of 3 vertices, 3 UVs, one
    ///     face, and <paramref name="textureCount" /> 2x2 skins. Vertex v of frame f is
    ///     <c>(v * 10, f * 100 + v, -v)</c> — asymmetric in all three components so an axis swap
    ///     cannot pass unnoticed.
    /// </summary>
    private static byte[] MeshBytes(int frames = 2, int textureCount = 1)
    {
        var body = new List<byte>();

        void U16(int value)
        {
            var word = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)value);
            body.AddRange(word);
        }

        void I16(int value)
        {
            var word = new byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(word, (short)value);
            body.AddRange(word);
        }

        U16(ShadowkeyMesh.FormatTag);
        U16(frames);
        U16(3); // vertices per frame
        U16(3); // uv pairs
        U16(1); // faces
        U16(9); // redundant coordinate count = 3 * vertexCount
        U16(ShadowkeyMesh.HeaderTrailer);

        for (var f = 0; f < frames; f++)
        {
            for (var v = 0; v < 3; v++)
            {
                I16(v * 10);
                I16((f * 100) + v);
                I16(-v);
            }
        }

        // UVs are 8.8 fixed point in texels: u = 0, 1, 2 and v = 1.5 throughout.
        for (var u = 0; u < 3; u++)
        {
            U16(u * 256);
            U16(0x0180);
        }

        // One face: vertices 0,1,2 and UVs 0,1,2.
        U16(0);
        U16(1);
        U16(2);
        U16(0);
        U16(1);
        U16(2);

        U16(textureCount);
        U16(TextureSize);
        U16(TextureSize);
        ushort[] texels = [0x0F00, 0x00F0, 0x000F, ShadowkeyMesh.MagentaColourKey];
        for (var t = 0; t < textureCount; t++)
        {
            foreach (var texel in texels)
            {
                U16(texel);
            }
        }

        U16(1); // one sequence
        U16(0);
        U16(frames);
        U16(5);

        return [.. body];
    }

    /// <summary>The normalised UVs of the one face, u = 0, 1, 2 texels over a 2-wide skin.</summary>
    private static readonly float[] ExpectedUvs = [0f, 0.75f, 0.5f, 0.75f, 1.0f, 0.75f];

    private static ShadowkeyMesh Parse(int frames = 2, int textureCount = 1) =>
        ShadowkeyMesh.Parse(MeshBytes(frames, textureCount), "rat.bin");

    [Fact]
    public void BuildMeshScene_ProducesOneMeshPartForTheWholeMesh()
    {
        var scene = ShadowkeySceneBuilder.BuildMeshScene(Parse());

        // Faces carry no texture index, so a Shadowkey mesh is always exactly one submesh.
        Assert.Single(scene.MeshParts);
        Assert.Equal("rat.bin", scene.MeshParts[0].Name);
        Assert.Equal(BethesdaViewerScenePurpose.ClassicMesh, scene.Purpose);
        Assert.Equal(BethesdaGame.Shadowkey, scene.Game);
    }

    /// <summary>
    ///     ClassicMesh must NOT be RawNif: that value opts a scene into the native sky pass, which
    ///     would draw a sky behind an inspected creature.
    /// </summary>
    [Fact]
    public void BuildMeshScene_DoesNotClaimToBeARawNif()
    {
        Assert.NotEqual(
            BethesdaViewerScenePurpose.RawNif,
            ShadowkeySceneBuilder.BuildMeshScene(Parse()).Purpose);
    }

    [Fact]
    public void BuildMeshScene_UnrollsOneCornerPerFaceVertex()
    {
        var submesh = ShadowkeySceneBuilder.BuildMeshScene(Parse()).MeshParts[0].Submesh;

        Assert.Equal(3 * 3, submesh.Positions.Length); // 1 face * 3 corners * xyz
        Assert.Equal(3, submesh.Triangles.Length);
        Assert.Equal(new ushort[] { 0, 1, 2 }, submesh.Triangles);
        Assert.NotNull(submesh.UVs);
        Assert.Equal(3 * 2, submesh.UVs!.Length);
    }

    /// <summary>
    ///     Source order keeps the file axes, where the SECOND component is up. Corner v is
    ///     (v * 10, v, -v) in frame 0.
    /// </summary>
    [Fact]
    public void BuildMeshScene_SourceAxes_KeepsFileOrder()
    {
        var submesh = ShadowkeySceneBuilder
            .BuildMeshScene(Parse(), axes: ShadowkeyAxisConvention.Source)
            .MeshParts[0].Submesh;

        Assert.Equal(new[] { 0f, 0f, 0f, 10f, 1f, -1f, 20f, 2f, -2f }, submesh.Positions);
    }

    /// <summary>
    ///     Z-up swaps the last two components so the file's up axis lands on Z, which is what the
    ///     Bethesda viewer treats as up. Corner v becomes (v * 10, -v, v).
    /// </summary>
    [Fact]
    public void BuildMeshScene_ZUpAxes_MovesTheUpAxisToZ()
    {
        var submesh = ShadowkeySceneBuilder
            .BuildMeshScene(Parse(), axes: ShadowkeyAxisConvention.ZUp)
            .MeshParts[0].Submesh;

        Assert.Equal(new[] { 0f, 0f, 0f, 10f, -1f, 1f, 20f, -2f, 2f }, submesh.Positions);
    }

    /// <summary>
    ///     UVs arrive in texels and must be divided by the SKIN dimensions. u = 0, 1, 2 texels over
    ///     a 2-wide skin is 0, 0.5, 1.0; v = 1.5 texels over a 2-tall skin is 0.75 throughout.
    ///     The u = 1.0 value is deliberately at the edge — values past 1 are legal and wrap.
    /// </summary>
    [Fact]
    public void BuildMeshScene_NormalisesTexelUvsByTheSkinSize()
    {
        var submesh = ShadowkeySceneBuilder.BuildMeshScene(Parse()).MeshParts[0].Submesh;

        Assert.Equal(ExpectedUvs, submesh.UVs!);
    }

    [Fact]
    public void BuildMeshScene_RegistersTheSkinAsAGeneratedTexture()
    {
        var scene = ShadowkeySceneBuilder.BuildMeshScene(Parse());
        var key = scene.MeshParts[0].Submesh.DiffuseTexturePath;

        Assert.Equal(ShadowkeySceneBuilder.SkinTextureKey("rat.bin", 0), key);
        Assert.True(scene.TryGetGeneratedTexture(key!, out var texture));
        Assert.Equal(TextureSize, texture!.Width);
        Assert.Equal(TextureSize, texture.Height);
    }

    /// <summary>
    ///     Extra textures are alternative whole-mesh skins, so selecting one changes the texture
    ///     key and the pixels while leaving the geometry identical.
    /// </summary>
    [Fact]
    public void BuildMeshScene_SelectsTheRequestedSkin()
    {
        var scene = ShadowkeySceneBuilder.BuildMeshScene(Parse(textureCount: 3), skin: 2);
        var key = scene.MeshParts[0].Submesh.DiffuseTexturePath;

        Assert.Equal(ShadowkeySceneBuilder.SkinTextureKey("rat.bin", 2), key);
        Assert.True(scene.TryGetGeneratedTexture(key!, out _));
    }

    /// <summary>
    ///     The magenta colour key decodes to alpha 0 by default. The synthetic skin puts it in the
    ///     last of four texels, so exactly one pixel must be transparent.
    /// </summary>
    [Fact]
    public void BuildMeshScene_HonoursTheMagentaColourKey()
    {
        var scene = ShadowkeySceneBuilder.BuildMeshScene(Parse());
        scene.TryGetGeneratedTexture(scene.MeshParts[0].Submesh.DiffuseTexturePath!, out var texture);

        var transparent = 0;
        for (var i = 3; i < texture!.Pixels.Length; i += 4)
        {
            if (texture.Pixels[i] == 0)
            {
                transparent++;
            }
        }

        Assert.Equal(1, transparent);
    }

    [Fact]
    public void BuildMeshScene_WithoutTheColourKey_LeavesEveryTexelOpaque()
    {
        var scene = ShadowkeySceneBuilder.BuildMeshScene(Parse(), magentaIsTransparent: false);
        scene.TryGetGeneratedTexture(scene.MeshParts[0].Submesh.DiffuseTexturePath!, out var texture);

        for (var i = 3; i < texture!.Pixels.Length; i += 4)
        {
            Assert.Equal(255, texture.Pixels[i]);
        }
    }

    /// <summary>Frame 1 shifts every vertex up by 100 in the source up axis (component 1).</summary>
    [Fact]
    public void BuildMeshScene_SelectsTheRequestedFrame()
    {
        var frame0 = ShadowkeySceneBuilder
            .BuildMeshScene(Parse(), frame: 0, axes: ShadowkeyAxisConvention.Source)
            .MeshParts[0].Submesh.Positions;
        var frame1 = ShadowkeySceneBuilder
            .BuildMeshScene(Parse(), frame: 1, axes: ShadowkeyAxisConvention.Source)
            .MeshParts[0].Submesh.Positions;

        for (var corner = 0; corner < 3; corner++)
        {
            Assert.Equal(frame0[(corner * 3) + 1] + 100f, frame1[(corner * 3) + 1]);
        }
    }

    [Fact]
    public void BuildMeshScene_BoundsSpanTheGeometry()
    {
        var scene = ShadowkeySceneBuilder.BuildMeshScene(Parse(), axes: ShadowkeyAxisConvention.Source);

        Assert.NotNull(scene.Bounds);
        Assert.Equal(0f, scene.Bounds!.Value.Minimum.X);
        Assert.Equal(20f, scene.Bounds.Value.Maximum.X);
        Assert.Equal(-2f, scene.Bounds.Value.Minimum.Z);
        Assert.Equal(0f, scene.Bounds.Value.Maximum.Z);
    }

    [Fact]
    public void BuildMeshScene_RejectsAFrameOutsideTheRecord()
    {
        Assert.ThrowsAny<ArgumentException>(() => ShadowkeySceneBuilder.BuildMeshScene(Parse(), frame: 5));
    }

    [Fact]
    public void BuildMeshScene_RejectsASkinOutsideTheRecord()
    {
        Assert.ThrowsAny<ArgumentException>(() => ShadowkeySceneBuilder.BuildMeshScene(Parse(), skin: 4));
    }
}

using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.VanBuren;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Hand-built B3D token streams with pinned decoded values, laid out exactly as
///     <c>F3.exe</c>'s <c>FUN_004de680</c> / <c>FUN_004dabd0</c> / <c>FUN_004dc330</c> consume them.
///     Both mesh encodings are covered, and the scene-flag discrimination between them.
/// </summary>
public sealed class VanBurenB3dMeshTests
{
    /// <summary>
    ///     The three vertices of one right triangle, with unit normals, wound the way retail winds
    ///     (cross(b−a, c−a) · n &gt; 0 on the raw coordinates: (0,0,1)−(0,0,0) × (1,0,0)−(0,0,0) = +Y).
    /// </summary>
    private static readonly (Vector3 P, Vector3 N, Vector2 Uv)[] Tri =
    [
        (new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector2(0, 0)),
        (new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector2(0, 1)),
        (new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector2(1, 0))
    ];

    /// <summary>Scene token 7: a material named <paramref name="name" />.</summary>
    private static void Material(Stream s, string name, string blend)
    {
        s.U8(0x07).Str("BASE_2X").Str(name);
        for (var i = 0; i < 16; i++)
        {
            s.F32(0.5f);
        }

        s.F32(1f).F32(2f).Str(blend).Str("SILENT").U32(0).U32(0);
    }

    /// <summary>Scene token 0x0E: a bone with translation + rotation (flags 3).</summary>
    private static void Bone(Stream s, string name, float tx)
    {
        s.U8(0x0E).Str(name).Str(string.Empty).U8(3).V3(tx, 0.17f, -0.008f).V3(1, 0, 0).F32(180f);
    }

    private static byte[] ConvertedPayload(bool skinned)
    {
        var s = new Stream();
        s.U8(0x1C).U8(1); // the flag: meshes carry a version byte
        s.U8(0x03).F32(1f).U8(0x04).F32(1024f).U8(0x05).Str("scene.max").U8(0x16).U32(7);
        Material(s, "Skin", "OPAQUE");
        Bone(s, "Root", 0f);
        Bone(s, "Spine_1", 0.5f);
        s.U8(0x0A).U8(0x0C).Str("Scene Root");
        s.U8(0x0A).U8(0x0C).Str("CR_Test");
        s.U8(0x0F).Str(string.Empty).U8(1); // mesh attribute, version 1 = converted
        s.U32(3).U32(44);
        foreach (var (p, n, uv) in Tri)
        {
            s.V3(p.X, p.Y, p.Z).V3(n.X, n.Y, n.Z).U32(0xFF7F7F7F).F32(uv.X).F32(uv.Y).F32(0.25f).F32(0.75f);
        }

        s.V3(0, 0, 0).V3(1, 0, 1); // declared bounds
        if (skinned)
        {
            s.U8(1).U32(4);
            for (var stream = 0; stream < 4; stream++)
            {
                for (var v = 0; v < 3; v++)
                {
                    if (stream == 0)
                    {
                        s.U32(1).F32(0.75f);
                    }
                    else if (stream == 1)
                    {
                        s.U32(0).F32(0.25f);
                    }
                    else
                    {
                        s.U32(0xFFFFFFFF).F32(0f);
                    }
                }
            }
        }
        else
        {
            s.U8(0);
        }

        s.U32(1); // one group
        s.Str("Skin").Str("BASE_2X");
        for (var i = 0; i < 16; i++)
        {
            s.F32(0.5f);
        }

        s.F32(8f).Str("OPAQUE").Str("SILENT");
        for (var i = 0; i < 60; i++)
        {
            s.U8(0);
        } // render-state block

        s.U32(1).Str("CR_Test_LG.tga"); // one texture
        s.U32(1).Str("Skin"); // name words
        s.Str("Skin"); // the per-group string list
        s.U32(3); // index count
        s.U32(1); // triangle count
        s.U16(0).U16(1).U16(2);
        s.U8(0x0B).U8(0x0B); // end child node, end root node
        s.U8(0x01);
        return s.Bytes();
    }

    private static byte[] SourcePayload(bool flagged)
    {
        var s = new Stream();
        if (flagged)
        {
            s.U8(0x1C).U8(1);
        }

        s.U8(0x03).F32(1f);
        Material(s, "Blood", "ALPHABLEND");
        s.U8(0x0A).U8(0x0C).Str("Scene Root");
        s.U8(0x0A).U8(0x0C).Str("Cone_blood");
        s.U8(0x0F).Str(string.Empty);
        if (flagged)
        {
            s.U8(0); // version 0 = source form
        }

        s.U32(3);
        foreach (var (p, n, uv) in Tri)
        {
            s.V3(p.X, p.Y, p.Z).V3(n.X, n.Y, n.Z).F32(1).F32(1).F32(1).F32(1);
            s.U32(2).F32(uv.X).F32(uv.Y).F32(0.25f).F32(0.75f); // two UV sets
            s.U32(0); // no bone weights
        }

        s.U32(1); // one group
        s.U32(0).Str("Blood").U32(1).Str("blood_01.tga").U32(0);
        s.U8(201);
        s.U32(3).U32(0).U32(1).U32(2);
        s.U8(0).U8(0).U8(0);
        s.U8(0x0B).U8(0x0B).U8(0x01);
        return s.Bytes();
    }

    [Fact]
    public void Parse_DecodesAConvertedMeshToPinnedValues()
    {
        var file = VanBurenB3DFile.Parse(ConvertedPayload(true), "Critters.grp/00000");

        Assert.True(file.Versioned);
        Assert.Equal(["Scene Root", "CR_Test"], file.NodeNames);
        Assert.Equal("CR_Test", file.MeshName);
        Assert.Equal("scene.max", file.HeaderString);
        Assert.Equal(1024f, file.HeaderFloatB);
        Assert.Equal(7u, file.HeaderDword);
        Assert.Equal(["Root", "Spine_1"], file.Bones.Select(b => b.Name));
        Assert.Equal(new Vector3(0.5f, 0.17f, -0.008f), file.Bones[1].Translation);
        Assert.Equal(180f, file.Bones[1].RotationDegrees);
        Assert.Equal(Vector3.UnitX, file.Bones[1].RotationAxis);
        Assert.Equal("Skin", Assert.Single(file.Materials).Name);

        var mesh = Assert.Single(file.Meshes);
        Assert.Equal(VanBurenB3dMeshForm.Converted, mesh.Form);
        Assert.Equal("CR_Test", mesh.NodeName);
        Assert.Equal(3, mesh.Vertices.Count);
        Assert.Equal(new Vector3(0, 0, 1), mesh.Vertices[1].Position);
        Assert.Equal(new Vector3(1, 0, 0), mesh.Vertices[2].Position);
        Assert.Equal(Vector3.UnitY, mesh.Vertices[1].Normal);
        Assert.Equal(new Vector2(1, 0), mesh.Vertices[2].TexCoord);
        Assert.Equal(new Vector2(0.25f, 0.75f), mesh.Vertices[1].LightmapCoord);
        Assert.Equal(new Vector4(127 / 255f, 127 / 255f, 127 / 255f, 1f), mesh.Vertices[0].Colour);
        Assert.Equal(new Vector3(1, 0, 1), mesh.DeclaredBoundsMax);
        Assert.Equal((Vector3.Zero, new Vector3(1, 0, 1)), mesh.ComputeBounds());

        var group = Assert.Single(mesh.Groups);
        Assert.Equal("Skin", group.Name);
        Assert.Equal("BASE_2X", group.Shader);
        Assert.Equal("OPAQUE", group.BlendState);
        Assert.Equal("SILENT", group.SurfaceSound);
        Assert.Equal(["CR_Test_LG.tga"], group.Textures);
        Assert.Equal([0, 1, 2], group.Indices);
        Assert.Equal(1, mesh.TriangleCount);

        Assert.True(mesh.Skinned);
        Assert.Equal(3, mesh.BoneWeights!.Count);
        Assert.Equal([new VanBurenB3DBoneWeight(1, 0.75f), new VanBurenB3DBoneWeight(0, 0.25f)], mesh.BoneWeights[2]);
    }

    [Fact]
    public void Parse_DecodesASourceMeshInBothFlavours()
    {
        foreach (var flagged in new[] { false, true })
        {
            var file = VanBurenB3DFile.Parse(SourcePayload(flagged), flagged ? "Tiles.grp" : "Props.grp");

            Assert.Equal(flagged, file.Versioned);
            Assert.Equal("Cone_blood", file.MeshName);
            var mesh = Assert.Single(file.Meshes);
            Assert.Equal(VanBurenB3dMeshForm.Source, mesh.Form);
            Assert.False(mesh.Skinned);
            Assert.Null(mesh.DeclaredBoundsMin);
            Assert.Equal(new Vector3(0, 0, 1), mesh.Vertices[1].Position);
            Assert.Equal(new Vector2(1, 0), mesh.Vertices[2].TexCoord);
            Assert.Equal(new Vector2(0.25f, 0.75f), mesh.Vertices[2].LightmapCoord);
            Assert.Equal(Vector4.One, mesh.Vertices[0].Colour);

            var group = Assert.Single(mesh.Groups);
            Assert.Equal("Blood", group.Name);
            Assert.Equal(["blood_01.tga"], group.Textures);
            Assert.Equal([0, 1, 2], group.Indices);
            // Shader/blend/sound come from the scene material of the same name.
            Assert.Equal("ALPHABLEND", group.BlendState);
            Assert.Equal("BASE_2X", group.Shader);
        }
    }

    [Fact]
    public void Parse_RejectsBytesAfterTheEofToken()
    {
        // Exact tiling is the gate: a trailing byte is a failure, not a trailer.
        var bytes = ConvertedPayload(false).Concat(new byte[] { 0 }).ToArray();
        var e = Assert.Throws<InvalidDataException>(() => VanBurenB3DFile.Parse(bytes, "x"));
        Assert.Contains("after the EOF token", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnUnknownSceneToken()
    {
        // The 3 retail payloads the engine's own switch refuses open with 0x35.
        var s = new Stream().U8(0x35).U8(0x01);
        var e = Assert.Throws<InvalidDataException>(() => VanBurenB3DFile.Parse(s.Bytes(), "Critters.grp/01322"));
        Assert.Contains("0x35", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAConvertedMeshWithWrongBoneStreamCount()
    {
        var bytes = ConvertedPayload(true);
        // The stream count sits right after the skin flag, which follows 3 vertices x 44 + 24 bytes of bounds.
        var marker = Encoding.ASCII.GetBytes("CR_Test");
        var meshStart = IndexOf(bytes, marker) + marker.Length + 1 + 2 + 1; // 0x0F, empty string, version
        var streamCountOffset = meshStart + 8 + 3 * 44 + 24 + 1;
        Assert.Equal(4u, BitConverter.ToUInt32(bytes, streamCountOffset));
        bytes[streamCountOffset] = 3;
        var e = Assert.Throws<InvalidDataException>(() => VanBurenB3DFile.Parse(bytes, "x"));
        Assert.Contains("Bone counts different", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnIndexBeyondTheVertexCount()
    {
        var bytes = ConvertedPayload(false);
        // The u16 index list is the last 6 bytes before "0B 0B 01".
        bytes[^5] = 9;
        var e = Assert.Throws<InvalidDataException>(() => VanBurenB3DFile.Parse(bytes, "x"));
        Assert.Contains("exceeds 3 vertices", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsB3d_ChecksTheSignature()
    {
        Assert.True(VanBurenB3DFile.IsB3d(ConvertedPayload(false)));
        Assert.False(VanBurenB3DFile.IsB3d("B3D 1.0 \x01"u8.ToArray()));
        Assert.False(VanBurenB3DFile.TryParse("EEN2....."u8.ToArray(), "e", out _, out var error));
        Assert.Contains("signature", error, StringComparison.Ordinal);
    }

    [Fact]
    public void GlbExport_MirrorsZAndReversesTheWindingKeepingYUp()
    {
        var file = VanBurenB3DFile.Parse(ConvertedPayload(false), "x");
        var glb = VanBurenB3DGlbExporter.WriteToBytes(file);

        using var stream = new MemoryStream(glb);
        var model = ModelRoot.ReadGLB(stream);
        var mesh = Assert.Single(model.LogicalMeshes);
        Assert.Equal("CR_Test", mesh.Name);
        var primitive = Assert.Single(mesh.Primitives);
        Assert.Equal("CR_Test_LG.tga", primitive.Material.Name);
        Assert.True(primitive.Material.DoubleSided);

        var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
        Assert.Equal(3, positions.Count);
        Assert.Contains(new Vector3(0, 0, -1), positions); // the left-handed Z is mirrored
        Assert.Contains(new Vector3(1, 0, 0), positions); // X and Y pass through
        Assert.DoesNotContain(new Vector3(0, 0, 1), positions);
        var normals = primitive.GetVertexAccessor("NORMAL").AsVector3Array();
        Assert.All(normals, n => Assert.Equal(Vector3.UnitY, n));
        var uvs = primitive.GetVertexAccessor("TEXCOORD_0").AsVector2Array();
        Assert.Contains(new Vector2(1, 0), uvs);
        Assert.Equal(3, primitive.GetIndices().Count);

        // Front faces are counter-clockwise for glTF: the exported winding agrees with the normal.
        // A mirror without the index swap would give a negative product here; a pass-through export
        // would keep (0, 0, 1) above.
        var (a, b, c) = primitive.GetTriangleIndices().Single();
        var cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
        Assert.True(Vector3.Dot(cross, Vector3.UnitY) > 0, cross.ToString());
    }

    [Fact]
    public void Parse_ConsumesTheNodeAttributesNoRetailPayloadCarries()
    {
        // Sizes read off the decompile alone (never exercised by retail): 0x14 emitter = one string
        // (FUN_004d23e0), 0x15 lightmap surface = two strings (FUN_004d0f30), 0x13 camera = name + 3
        // floats (FUN_004d2200), 0x17 water = name + two strings + 28 bytes (FUN_004d26d0),
        // 0x11 spot light = name + 80 bytes (FUN_004d1ff0). Exact tiling is the gate, so a size that
        // drifted by one byte would leave the stream mis-aligned at the next token.
        var s = new Stream();
        s.U8(0x0A).U8(0x0C).Str("Scene Root");
        s.U8(0x14).Str("emitter");
        s.U8(0x15).Str("lightmap").Str("surface");
        s.U8(0x13).Str("camera").F32(1).F32(2).F32(3);
        s.U8(0x17).Str("water").Str("a").Str("b");
        for (var i = 0; i < 28; i++)
        {
            s.U8((byte)i);
        }

        s.U8(0x11).Str("spot");
        for (var i = 0; i < 80; i++)
        {
            s.U8((byte)i);
        }

        s.U8(0x1B).U32(2).Str("zone1").Str("zone2");
        s.U8(0x0B).U8(0x01);

        var file = VanBurenB3DFile.Parse(s.Bytes(), "synthetic");
        Assert.Equal(["Scene Root"], file.NodeNames);
        Assert.Empty(file.Meshes);

        // One byte more or less and the walk lands on a non-token.
        var longer = s.Bytes().ToList();
        longer.Insert(longer.Count - 2, 0);
        Assert.Throws<InvalidDataException>(() => VanBurenB3DFile.Parse([.. longer], "synthetic"));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class Stream
    {
        public readonly List<byte> B = [.. Encoding.ASCII.GetBytes(VanBurenB3DFile.Signature)];

        public Stream U8(byte v)
        {
            B.Add(v);
            return this;
        }

        public Stream U16(ushort v)
        {
            B.AddRange(BitConverter.GetBytes(v));
            return this;
        }

        public Stream U32(uint v)
        {
            B.AddRange(BitConverter.GetBytes(v));
            return this;
        }

        public Stream F32(float v)
        {
            B.AddRange(BitConverter.GetBytes(v));
            return this;
        }

        public Stream V3(float x, float y, float z)
        {
            return F32(x).F32(y).F32(z);
        }

        public Stream Str(string s)
        {
            U16((ushort)s.Length);
            B.AddRange(Encoding.ASCII.GetBytes(s));
            return this;
        }

        public byte[] Bytes()
        {
            return [.. B];
        }
    }
}

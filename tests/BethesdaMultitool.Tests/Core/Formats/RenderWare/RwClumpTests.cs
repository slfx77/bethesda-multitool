using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.RenderWare;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

public sealed class RwClumpTests
{
    private static readonly int[] SharedGeometryAtomicFrames = [1, 0];

    [Fact]
    public void DecodesAuthoredFramesMaterialsAndAtomicReferences()
    {
        var clump = Read(Build());
        Assert.Equal(0x1C020065u, clump.LibraryId);
        Assert.Equal(2, clump.Frames.Count);
        Assert.Equal(-1, clump.Frames[0].ParentIndex);
        Assert.Equal(0, clump.Frames[1].ParentIndex);
        Assert.Equal(new Vector3(10, 21, 30), Vector3.Transform(Vector3.UnitX, clump.Frames[0].LocalTransform));
        Assert.Equal(new Vector3(2, 3, 4), clump.Frames[1].LocalTransform.Translation);
        Assert.Equal(17u, clump.Frames[1].Flags);
        var atomic = Assert.Single(clump.Atomics);
        Assert.Equal((1, 0, 5u, 0x89ABCDEFu), (atomic.FrameIndex, atomic.GeometryIndex, atomic.Flags, atomic.Unused));
        var mesh = Assert.Single(clump.Geometries);
        Assert.Equal(4, mesh.Geometry.Positions.Length);
        Assert.Equal(new RwTriangle(0, 1, 2, 0), mesh.Geometry.Triangles[0]);
        Assert.Equal(new RwTriangle(2, 1, 3, 1), mesh.Geometry.Triangles[1]);
        Assert.Equal(new[] { -1, -1 }, mesh.MaterialMap);
        Assert.Equal(2, mesh.Materials.Count);
        var material = mesh.Materials[0];
        Assert.Equal((7u, 0x80402010u, 0x12345678u, 1u),
            (material.Flags, material.ColorRgba, material.Unused, material.Textured));
        Assert.Equal((0.25f, 0.5f, 0.75f), (material.Ambient, material.Specular, material.Diffuse));
        Assert.NotNull(material.Texture);
        Assert.Equal((0x11102u, "first", "mask"),
            (material.Texture.Sampler, material.Texture.Name, material.Texture.MaskName));
        Assert.Equal("second", mesh.Materials[1].Texture!.Name);
        Assert.Empty(clump.Diagnostics);
    }

    [Fact]
    public void OwnsSourceBytesAndDecodedArraysIndependentlyOfCaller()
    {
        var bytes = Build(opaquePlugins: true);
        var before = bytes.ToArray();
        var clump = Read(bytes);
        Assert.Equal(before, bytes);
        var position = clump.Geometries[0].Geometry.Positions[1];
        var plugin = Assert.Single(clump.Extensions);
        Assert.Equal(new byte[] { 1, 2, 3 }, plugin.Payload.ToArray());
        Array.Fill(bytes, (byte)0);
        Assert.Equal(before, clump.Source.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, plugin.Payload.ToArray());
        Assert.Equal(position, clump.Geometries[0].Geometry.Positions[1]);
        clump.Geometries[0].Geometry.Positions[1] = new Vector3(999);
        Assert.Equal(before, clump.Source.ToArray());
    }

    [Fact]
    public void PreservesOpaquePluginsAndReportsTheirUnimplementedSemantics()
    {
        var clump = Read(Build(opaquePlugins: true));
        Assert.Contains(clump.Diagnostics, d => d.Scope == "frame[1]" && d.ChunkType == 0x11E);
        Assert.Contains(clump.Diagnostics, d => d.Scope == "geometry[0]" && d.ChunkType == RwChunk.SkinPlugin);
        Assert.Contains(clump.Diagnostics, d => d.Scope == "clump" && d.ChunkType == 0xCAFE);
        var skin = Assert.Single(clump.Geometries[0].Extensions, p => p.Type == RwChunk.SkinPlugin);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, skin.Payload.ToArray());
        Assert.Equal(0x1C020065u, skin.LibraryId);
        Assert.Equal(0x116u, BinaryPrimitives.ReadUInt32LittleEndian(clump.Source.Span[skin.HeaderOffset..]));
    }

    [Fact]
    public void MultipleAtomicsCanReferenceOneGeometryWithoutFlatteningFrames()
    {
        var clump = Read(Build(twoAtomics: true));
        Assert.Single(clump.Geometries);
        Assert.Equal(2, clump.Atomics.Count);
        Assert.Equal(SharedGeometryAtomicFrames, clump.Atomics.Select(a => a.FrameIndex));
        Assert.All(clump.Atomics, a => Assert.Equal(0, a.GeometryIndex));
    }

    [Fact]
    public void MaterialReuseMapRetainsIdentityAndDoesNotConsumeAnotherChunk()
    {
        var source = Build();
        source = Rewrite(source, [0x1A, 0xF, 8], _ => Join(
            Chunk(1, Words(2, uint.MaxValue, 0)), Material("reused")));
        var mesh = Assert.Single(Read(source).Geometries);
        Assert.Equal(new[] { -1, 0 }, mesh.MaterialMap);
        Assert.Same(mesh.Materials[0], mesh.Materials[1]);
    }

    [Fact]
    public void StripParityAndDegenerateConnectorsPreserveAuthoredTopology()
    {
        // One material and a single strip: ABC, CBD. Duplicated indices connect another ABC.
        var source = Build();
        source = Rewrite(source, [0x1A, 0xF, 1], _ => GeometryBody(
            [(0, 1, 2, 0), (2, 1, 3, 0), (0, 1, 2, 0), (0, 1, 1, 0)]));
        source = Rewrite(source, [0x1A, 0xF, 3, 0x50E], _ =>
            Words(1, 1, 9, 9, 0, 0, 1, 2, 3, 3, 0, 0, 1, 2));
        var mesh = Assert.Single(Read(source).Geometries);
        Assert.Equal(4, mesh.Geometry.Triangles.Length);
        Assert.Equal(new RwTriangle(0, 1, 1, 0), mesh.Geometry.Triangles[3]);
        Assert.Equal(9, Assert.Single(mesh.BinMesh.Splits).Indices.Length);
    }

    [Fact]
    public void TriangleListBinMeshPreservesCyclicWinding()
    {
        var source = Rewrite(Build(), [0x1A, 0xF, 3, 0x50E], _ =>
            Words(0, 2, 6, 3, 0, 1, 2, 0, 3, 1, 1, 3, 2));
        Assert.False(Read(source).Geometries[0].BinMesh.IsTriangleStrip);
    }

    [Theory]
    [InlineData(0x05u)]
    [InlineData(0x12u)]
    public void CameraAndLightAreRetainedWithExplicitSemanticDiagnostic(uint type)
    {
        var clump = Read(Build(attachment: type));
        var attachment = Assert.Single(clump.Attachments);
        Assert.Equal(1, attachment.FrameIndex);
        Assert.Equal(type, attachment.Chunk.Type);
        Assert.Contains(clump.Diagnostics, diagnostic => diagnostic.ChunkType == type);
        Assert.NotEmpty(attachment.Chunk.Payload.ToArray());
    }

    [Theory]
    [InlineData("root truncation")]
    [InlineData("root tail")]
    [InlineData("wrong root")]
    [InlineData("nested tail")]
    [InlineData("nested size")]
    [InlineData("duplicate struct")]
    [InlineData("frame count")]
    [InlineData("frame extensions")]
    [InlineData("frame parent")]
    [InlineData("frame cycle")]
    [InlineData("frame nonfinite")]
    [InlineData("geometry count")]
    [InlineData("native geometry")]
    [InlineData("extra morph")]
    [InlineData("geometry short header")]
    [InlineData("geometry truncated morph")]
    [InlineData("geometry vertex overflow")]
    [InlineData("geometry triangle overflow")]
    [InlineData("geometry UV count")]
    [InlineData("geometry missing normals")]
    [InlineData("morph presence")]
    [InlineData("position nonfinite")]
    [InlineData("triangle vertex")]
    [InlineData("triangle material")]
    [InlineData("material reuse")]
    [InlineData("texture count")]
    [InlineData("texture terminator")]
    [InlineData("atomic count")]
    [InlineData("atomic frame")]
    [InlineData("atomic geometry")]
    [InlineData("bin total")]
    [InlineData("bin vertex")]
    [InlineData("bin material")]
    [InlineData("bin winding")]
    [InlineData("bin multiplicity")]
    [InlineData("bin incomplete list")]
    [InlineData("bin flags")]
    [InlineData("camera count")]
    [InlineData("camera frame")]
    public void RejectsMalformedOrUnsupportedCoreWithoutPartialGraph(string mutation)
    {
        var source = InvalidSource(mutation);
        var snapshot = source.ToArray();
        Assert.Null(RwClumpReader.TryRead(source, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error), mutation);
        Assert.Equal(snapshot, source);
    }

    private static byte[] InvalidSource(string mutation)
    {
        var source = Build();
        uint[] geometry = [0x1A, 0xF, 1];
        uint[] bin = [0x1A, 0xF, 3, 0x50E];
        return mutation switch
        {
            "root truncation" => source[..^1],
            "root tail" => [.. source, 0],
            "wrong root" => Set(source, 0, 0xB),
            "nested tail" => Rewrite(source, [0x1A, 0xF, 3], b => [.. b, 0]),
            "nested size" => Rewrite(source, [0x1A, 0xF, 3], b => Set(b, 4, uint.MaxValue)),
            "duplicate struct" => Rewrite(source, [0x14], b => [.. b, .. Chunk(1, Words(1, 0, 5, 0))]),
            "frame count" => Change(source, [0xE, 1], 0, 3),
            "frame extensions" => Rewrite(source, [0xE], b => b[..^12]),
            "frame parent" => Change(source, [0xE, 1], 52, 2),
            "frame cycle" => Change(source, [0xE, 1], 52, 1),
            "frame nonfinite" => Change(source, [0xE, 1], 4, 0x7FC00000),
            "geometry count" => Change(source, [0x1A, 1], 0, 2),
            "native geometry" => Change(source, geometry, 0, 0x1010037),
            "extra morph" => Change(source, geometry, 12, 2),
            "geometry short header" => Rewrite(source, geometry, b => b[..15]),
            "geometry truncated morph" => Rewrite(source, geometry, b => b[..^1]),
            "geometry vertex overflow" => Change(source, geometry, 8, uint.MaxValue),
            "geometry triangle overflow" => Change(source, geometry, 4, uint.MaxValue),
            "geometry UV count" => Change(source, geometry, 0, 0xFF0037),
            "geometry missing normals" => Change(source, geometry, 0, 0x10027),
            "morph presence" => Change(source, geometry, 80, 0),
            "position nonfinite" => Change(source, geometry, 88, 0x7FC00000),
            "triangle vertex" => Rewrite(source, geometry, b => Set16(b, 48, 4)),
            "triangle material" => Rewrite(source, geometry, b => Set16(b, 52, 2)),
            "material reuse" => Change(source, [0x1A, 0xF, 8, 1], 4, 0),
            "texture count" => Change(source, [0x1A, 0xF, 8, 7, 1], 12, 0),
            "texture terminator" => Rewrite(source, [0x1A, 0xF, 8, 7, 6, 2], b => Enumerable.Repeat((byte)65, b.Length).ToArray()),
            "atomic count" => Change(source, [1], 0, 2),
            "atomic frame" => Change(source, [0x14, 1], 0, 2),
            "atomic geometry" => Change(source, [0x14, 1], 4, 1),
            "bin total" => Change(source, bin, 8, 7),
            "bin vertex" => Change(source, bin, 20, 4),
            "bin material" => Change(source, bin, 16, 2),
            "bin winding" => Rewrite(source, bin, b => Set(Set(b, 20, 1), 24, 0)),
            "bin multiplicity" => Rewrite(source, bin, _ => Words(0, 2, 9, 6, 0, 0, 1, 2, 0, 1, 2, 3, 1, 2, 1, 3)),
            "bin incomplete list" => Rewrite(source, bin, _ => Words(0, 1, 2, 2, 0, 0, 1)),
            "bin flags" => Change(source, bin, 0, 2),
            "camera count" => Change(Build(attachment: 5), [1], 8, 0),
            "camera frame" => Rewrite(Build(attachment: 5), [], b => Set(b, FindAssociation(b), 2)),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
    }

    private static int FindAssociation(byte[] body)
    {
        for (var p = 0; p < body.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p + 4));
            if (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p)) == 1 && length == 4)
            {
                return p + 12;
            }

            p += 12 + length;
        }

        throw new InvalidOperationException("Missing test attachment.");
    }

    private static RwClump Read(byte[] source)
    {
        var result = RwClumpReader.TryRead(source, out var error);
        Assert.True(result is not null, error);
        Assert.NotNull(result);
        return result;
    }

    private static byte[] Build(bool opaquePlugins = false, bool twoAtomics = false, uint attachment = 0)
    {
        var frames = Bytes(w =>
        {
            w.Write(2u);
            foreach (var value in new float[] { 0, 1, 0, -1, 0, 0, 0, 0, 1, 10, 20, 30 })
            {
                w.Write(value);
            }

            w.Write(-1);
            w.Write(0u);
            foreach (var value in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 2, 3, 4 })
            {
                w.Write(value);
            }

            w.Write(0);
            w.Write(17u);
        });
        var geometry = Chunk(0xF, Join(Chunk(1, GeometryBody([(0, 1, 2, 0), (2, 1, 3, 1)])),
            Chunk(8, Join(Chunk(1, Words(2, uint.MaxValue, uint.MaxValue)), Material("first"), Material("second"))),
            Chunk(3, Join(Chunk(0x50E, Words(1, 2, 6, 3, 0, 0, 1, 2, 3, 1, 2, 1, 3)),
                opaquePlugins ? Chunk(0x116, [9, 8, 7, 6]) : []))));
        var atomic = Chunk(0x14, Join(Chunk(1, Words(1, 0, 5, 0x89ABCDEF)), Chunk(3, [])));
        return Chunk(0x10, Join(
            Chunk(1, Words(twoAtomics ? 2u : 1u, attachment == 0x12 ? 1u : 0u, attachment == 5 ? 1u : 0u)),
            Chunk(0xE, Join(Chunk(1, frames), Chunk(3, []), Chunk(3, opaquePlugins ? Chunk(0x11E, Words(256, 0, 0)) : []))),
            Chunk(0x1A, Join(Chunk(1, Words(1)), geometry)), atomic,
            twoAtomics ? Chunk(0x14, Join(Chunk(1, Words(0, 0, 5, 0)), Chunk(3, []))) : [],
            attachment == 0 ? [] : Join(Chunk(1, Words(1)), Chunk(attachment, Join(Chunk(1, Words(7, 8, 9)), Chunk(3, [])))),
            Chunk(3, opaquePlugins ? Chunk(0xCAFE, [1, 2, 3]) : [])));
    }

    private static byte[] GeometryBody((ushort A, ushort B, ushort C, ushort Material)[] triangles) => Bytes(w =>
    {
        w.Write(0x10037u);
        w.Write((uint)triangles.Length);
        w.Write(4u);
        w.Write(1u);
        foreach (var uv in new Vector2[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1) })
        {
            w.Write(uv.X);
            w.Write(uv.Y);
        }

        foreach (var t in triangles)
        {
            w.Write(t.B);
            w.Write(t.A);
            w.Write(t.Material);
            w.Write(t.C);
        }

        w.Write(0f);
        w.Write(0f);
        w.Write(0f);
        w.Write(2f);
        w.Write(1u);
        w.Write(1u);
        foreach (var p in new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new(1, 1, 0) })
        {
            w.Write(p.X);
            w.Write(p.Y);
            w.Write(p.Z);
        }

        for (var i = 0; i < 4; i++)
        {
            w.Write(0f);
            w.Write(0f);
            w.Write(1f);
        }
    });

    private static byte[] Material(string name) => Chunk(7, Join(
        Chunk(1, Join(Words(7, 0x80402010, 0x12345678, 1), Bytes(w => { w.Write(0.25f); w.Write(0.5f); w.Write(0.75f); }))),
        Chunk(6, Join(Chunk(1, Words(0x11102)), Chunk(2, Encoding.Latin1.GetBytes(name + '\0')),
            Chunk(2, Encoding.Latin1.GetBytes("mask\0")), Chunk(3, []))), Chunk(3, [])));

    private static byte[] Chunk(uint type, byte[] body) => Join(Words(type, (uint)body.Length, 0x1C020065), body);
    private static byte[] Words(params uint[] values) => Bytes(w => { foreach (var value in values) { w.Write(value); } });
    private static byte[] Join(params byte[][] values) => values.SelectMany(v => v).ToArray();

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    private static byte[] Set(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        return bytes;
    }

    private static byte[] Set16(byte[] bytes, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        return bytes;
    }

    private static byte[] Change(byte[] source, uint[] path, int offset, uint value) =>
        Rewrite(source, path, b => Set(b, offset, value));

    // Independent test serializer: rebuild ancestor lengths so malformed children cannot be
    // rejected merely because the root length changed. Paths select the first matching sibling.
    private static byte[] Rewrite(byte[] chunk, uint[] path, Func<byte[], byte[]> rewrite)
    {
        var type = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        var body = chunk[12..];
        if (path.Length == 0)
        {
            return Chunk(type, rewrite(body));
        }

        for (var p = 0; p < body.Length;)
        {
            var childType = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p));
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(p + 4)) + 12);
            if (childType == path[0])
            {
                return Chunk(type, [.. body[..p], .. Rewrite(body[p..(p + size)], path[1..], rewrite), .. body[(p + size)..]]);
            }

            p += size;
        }

        throw new InvalidOperationException("Missing test chunk.");
    }
}

using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for the Xbox <c>.CLP</c> mesh section: a hand-built section laid out the way the
///     loader at <c>0x00076AD0</c> reads one — header, index buffer, then a length-prefixed vertex
///     buffer that ends exactly at the section end.
///     <para>
///         Every expected value here is written into the fixture by hand and read back as a literal,
///         so nothing is derived from the code under test. The 4-vertex strip is small enough that
///         its two triangles can be spelled out.
///     </para>
/// </summary>
public sealed class BosXboxMeshTests
{
    private const int IndexBufferOffset = BosXboxMesh.HeaderLength;

    /// <summary>
    ///     Builds a section around the given strip and vertex bytes. The stride is written nowhere:
    ///     the reader must derive it from the vertex count at <c>+0x20</c>.
    /// </summary>
    private static byte[] Section(
        ushort[] strip,
        byte[] vertexBytes,
        int vertexCount,
        byte[]? bonePalette = null,
        byte lod = 0,
        int? declaredIndexCount = null,
        int? vertexBufferLength = null)
    {
        var vertexBufferOffset = IndexBufferOffset + strip.Length * sizeof(ushort);
        var section = new byte[vertexBufferOffset + 4 + vertexBytes.Length];

        section[BosXboxMesh.LodByteOffset] = lod;
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(BosXboxMesh.VertexBufferPointerOffset),
            vertexBufferOffset);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(BosXboxMesh.IndexBufferPointerOffset),
            IndexBufferOffset);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(BosXboxMesh.VertexCountOffset), vertexCount);

        // BOTH 64-entry bone tables start out entirely unused, as 308 of the 313 shipped meshes do.
        section.AsSpan(BosXboxMesh.BonePaletteOffset, BosXboxMesh.BonePaletteLength)
            .Fill(BosXboxMesh.BonePaletteUnused);
        section.AsSpan(BosXboxMesh.SecondBonePaletteOffset, BosXboxMesh.BonePaletteLength)
            .Fill(BosXboxMesh.BonePaletteUnused);
        (bonePalette ?? []).CopyTo(section.AsSpan(BosXboxMesh.BonePaletteOffset));

        // The render flags must agree with the stride the buffer lengths force (the draw path
        // computes 0x20 + (bit 0x20 ? 6 : 0)), so a 38-byte fixture has to set the bit.
        if (vertexCount > 0 && vertexBytes.Length / vertexCount == 38)
        {
            section[BosXboxMesh.RenderFlagsOffset] = BosXboxMesh.HaloStrideFlag;
        }

        BinaryPrimitives.WriteInt32LittleEndian(
            section.AsSpan(BosXboxMesh.LodTableOffset + lod * BosXboxMesh.LodRowLength),
            declaredIndexCount ?? strip.Length);

        for (var i = 0; i < strip.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(section.AsSpan(IndexBufferOffset + i * sizeof(ushort)), strip[i]);
        }

        BinaryPrimitives.WriteInt32LittleEndian(
            section.AsSpan(vertexBufferOffset),
            vertexBufferLength ?? vertexBytes.Length);
        vertexBytes.CopyTo(section.AsSpan(vertexBufferOffset + 4));
        return section;
    }

    /// <summary>Four vertices of the 16-byte world layout: SHORT3 position, NORMSHORT3 normal, NORMSHORT2 uv.</summary>
    private static byte[] WorldVertices()
    {
        (short X, short Y, short Z, short Nx, short Ny, short Nz, short U, short V)[] rows =
        [
            (0, 0, 0, 32767, 0, 0, 0, 0),
            (100, 0, 0, 0, 32767, 0, 32767, 0),
            (0, 200, 0, 0, 0, 32767, 0, 32767),
            (100, 200, -300, 0, 0, -32767, 32767, 32767)
        ];

        var bytes = new byte[rows.Length * 16];
        for (var i = 0; i < rows.Length; i++)
        {
            var at = i * 16;
            var row = rows[i];
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at), row.X);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 2), row.Y);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 4), row.Z);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 6), row.Nx);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 8), row.Ny);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 10), row.Nz);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 12), row.U);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 14), row.V);
        }

        return bytes;
    }

    /// <summary>Two vertices of the 32-byte skin layout: the world 16 plus a FLOAT4 of (bone×4, weight, bone×4, weight).</summary>
    private static byte[] SkinnedVertices()
    {
        var world = WorldVertices();
        var bytes = new byte[2 * 32];
        (float LaneA, float WeightA, float LaneB, float WeightB)[] skin =
        [
            (0f, 1f, 0f, 0f),
            (8f, 0.25f, 4f, 0.75f)
        ];

        for (var i = 0; i < 2; i++)
        {
            world.AsSpan(i * 16, 16).CopyTo(bytes.AsSpan(i * 32));
            var at = i * 32 + 16;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), skin[i].LaneA);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 4), skin[i].WeightA);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 8), skin[i].LaneB);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 12), skin[i].WeightB);
        }

        return bytes;
    }

    [Fact]
    public void TryParse_DecodesAFourVertexStripAtTheWorldStride()
    {
        var section = Section([0, 1, 2, 3], WorldVertices(), 4);
        Assert.True(BosXboxMesh.TryParse(section, "world", out var mesh, out var error), error);

        // The stride is never stored: 64 bytes over the 4 vertices at +0x20 is the only source.
        Assert.Equal(16, mesh.Stride);
        Assert.False(mesh.IsSkinned);
        Assert.False(mesh.HasSmoothNormal);
        Assert.Equal(4, mesh.Vertices.Count);
        Assert.Equal(new ushort[] { 0, 1, 2, 3 }, mesh.StripIndices);

        Assert.Equal(new Vector3(100, 200, -300), mesh.Vertices[3].Position);
        Assert.Equal(new Vector3(0, 1, 0), mesh.Vertices[1].Normal);
        Assert.Equal(new Vector2(1, 1), mesh.Vertices[3].TexCoord);

        // A four-index strip is two triangles, the second wound the other way round.
        Assert.Equal(new[] { 0, 1, 2, 1, 3, 2 }, mesh.TriangleIndices);
        Assert.Equal(2, mesh.TriangleCount);
    }

    [Fact]
    public void TryParse_ReadsTheSkinningLanesAsBonePaletteSlotsTimesFour()
    {
        // Palette (11, 22, 30): three slots, so lane 8 is slot 2 and lane 4 is slot 1.
        var section = Section([0, 1, 0, 1], SkinnedVertices(), 2, [11, 22, 30]);
        Assert.True(BosXboxMesh.TryParse(section, "skin", out var mesh, out var error), error);

        Assert.Equal(32, mesh.Stride);
        Assert.True(mesh.IsSkinned);
        Assert.Equal(new byte[] { 11, 22, 30 }, mesh.BonePalette);

        Assert.Equal(0, mesh.Vertices[0].BoneA);
        Assert.Equal(1f, mesh.Vertices[0].WeightA);
        Assert.Equal(2, mesh.Vertices[1].BoneA);
        Assert.Equal(0.25f, mesh.Vertices[1].WeightA);
        Assert.Equal(1, mesh.Vertices[1].BoneB);
        Assert.Equal(0.75f, mesh.Vertices[1].WeightB);
    }

    /// <summary>
    ///     Two vertices of the 38-byte halo layout: the 32-byte skin vertex plus the extra
    ///     <c>NORMSHORT3</c> that vertex register 3 (DIFFUSE) is fed from.
    /// </summary>
    private static byte[] HaloVertices()
    {
        var skinned = SkinnedVertices();
        var bytes = new byte[2 * 38];
        (short X, short Y, short Z)[] extra = [(0, 0, 32767), (0, -32767, 0)];
        for (var i = 0; i < 2; i++)
        {
            skinned.AsSpan(i * 32, 32).CopyTo(bytes.AsSpan(i * 38));
            var at = i * 38 + 32;
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at), extra[i].X);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 2), extra[i].Y);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 4), extra[i].Z);
        }

        return bytes;
    }

    [Fact]
    public void TryParse_ReadsTheHaloLayoutsExtraNormShort3AsTheSmoothNormal()
    {
        // ⚠ The 38-byte stride is likewise never stored: 76 bytes over the 2 vertices at +0x20.
        // The two extra vectors are written here by hand as (0, 0, +1) and (0, −1, 0), so nothing
        // in the expectation comes from the reader. The field was called Tangent until 2026-09-08;
        // the retail measurement in BosXboxMeshRetailTests refutes that reading.
        var section = Section([0, 1, 0, 1], HaloVertices(), 2, [11, 22, 30]);
        Assert.True(BosXboxMesh.TryParse(section, "halo", out var mesh, out var error), error);

        Assert.Equal(38, mesh.Stride);
        Assert.True(mesh.IsSkinned);
        Assert.True(mesh.HasSmoothNormal);
        Assert.Equal(new Vector3(0, 0, 1), mesh.Vertices[0].SmoothNormal);
        Assert.Equal(new Vector3(0, -1, 0), mesh.Vertices[1].SmoothNormal);

        // The skinning lanes still decode, so the extra vector is read after them, not over them.
        Assert.Equal(2, mesh.Vertices[1].BoneA);
        Assert.Equal(0.25f, mesh.Vertices[1].WeightA);
    }

    [Fact]
    public void ToTriangleList_DropsTheDegenerateStitchesAndAlternatesTheWinding()
    {
        // Two sub-strips joined by the repeated 2, 3 stitch, which is how retail glues them.
        int[] expected = [0, 1, 2, 1, 3, 2, 4, 5, 6, 5, 7, 6];
        Assert.Equal(expected, BosXboxMesh.ToTriangleList(new ushort[] { 0, 1, 2, 3, 3, 4, 4, 5, 6, 7 }));
    }

    [Fact]
    public void TryParse_RefusesAnIndexBufferThatDoesNotEndAtTheVertexBuffer()
    {
        // The declared count is one short, so the buffers no longer meet — the tiling gate.
        var section = Section([0, 1, 2, 3], WorldVertices(), 4, declaredIndexCount: 3);
        Assert.False(BosXboxMesh.TryParse(section, "gap", out _, out var error));
        Assert.Contains("rather than at the vertex buffer", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RefusesAVertexBufferThatDoesNotEndAtTheSectionEnd()
    {
        var section = Section([0, 1, 2, 3], WorldVertices(), 4, vertexBufferLength: 48);
        Assert.False(BosXboxMesh.TryParse(section, "short", out _, out var error));
        Assert.Contains("does not end at the", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RefusesAVertexCountThatDoesNotDivideTheBufferIntoADeclaredStride()
    {
        // 64 bytes over 5 vertices is 12.8 — no stride at all.
        var section = Section([0, 1, 2, 3], WorldVertices(), 5);
        Assert.False(BosXboxMesh.TryParse(section, "stride", out _, out var error));
        Assert.Contains("do not divide", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RefusesAnIndexPastTheDeclaredVertexCount()
    {
        var section = Section([0, 1, 2, 9], WorldVertices(), 4);
        Assert.False(BosXboxMesh.TryParse(section, "range", out _, out var error));
        Assert.Contains("is past the declared", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RefusesSkinningLanesThatAreNotBonePaletteSlots()
    {
        // Lane 8 is slot 2, but the palette declares only one bone.
        var section = Section([0, 1, 0, 1], SkinnedVertices(), 2, [11]);
        Assert.False(BosXboxMesh.TryParse(section, "palette", out _, out var error));
        Assert.Contains("bone palette", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(8.5f)]
    [InlineData(-4f)]
    [InlineData(2147483648f)]
    [InlineData(float.MaxValue)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NaN)]
    public void TryParse_RefusesMalformedSkinningLaneBeforeIntegerConversion(float lane)
    {
        var vertices = SkinnedVertices();
        BinaryPrimitives.WriteSingleLittleEndian(vertices.AsSpan(32 + 16), lane);
        var section = Section([0, 1, 0, 1], vertices, 2, [11, 22, 30]);

        Assert.False(BosXboxMesh.TryParse(section, "palette", out _, out var error));
        Assert.Contains("bone palette", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ReadsTheShadowMeshHeaderFromTheSlotByte()
    {
        // Slot 15 puts the header at 240 (0xF0) — where retail's six smallest shadow meshes sit,
        // right at the end of the LOD table. Two vertices of ten bytes fit before the index buffer.
        var strip = new ushort[] { 0, 1, 2, 3 };
        var section = Section(strip, WorldVertices(), 4);

        // Push the index buffer 64 bytes further out so the shadow block has room in front of it.
        var indexBufferOffset = IndexBufferOffset + 64;
        var tail = section.AsSpan(IndexBufferOffset);
        var padded = new byte[indexBufferOffset + tail.Length];
        section.AsSpan(0, IndexBufferOffset).CopyTo(padded);
        tail.CopyTo(padded.AsSpan(indexBufferOffset));

        padded[BosXboxMesh.ShadowSlotOffset] = 15;
        BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(15 * 16), 2);
        BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(15 * 16 + 4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(BosXboxMesh.IndexBufferPointerOffset), indexBufferOffset);
        BinaryPrimitives.WriteInt32LittleEndian(
            padded.AsSpan(BosXboxMesh.VertexBufferPointerOffset),
            indexBufferOffset + strip.Length * sizeof(ushort));

        Assert.True(BosXboxMesh.TryParse(padded, "shadow", out var mesh, out var error), error);
        Assert.Equal(15, mesh.ShadowSlot);
        Assert.Equal(5, mesh.ShadowVertexCount);
    }

    [Fact]
    public void GlbExporter_TurnsTheGameZUpIntoGltfYUpAndWritesAGlb()
    {
        var section = Section([0, 1, 2, 3], WorldVertices(), 4);
        var mesh = BosXboxMesh.Parse(section, "world");

        // (x, y, z) -> (x, z, -y): the rotation about X that puts the ground plane at y = 0.
        Assert.Equal(
            new Vector3(100, -300, -200),
            BosXboxMeshGlbExporter.ToGltf(new Vector3(100, 200, -300)));

        var glb = BosXboxMeshGlbExporter.WriteToBytes(mesh);
        Assert.Equal("glTF"u8.ToArray(), glb.Take(4).ToArray());
        Assert.True(glb.Length > 512, $"the GLB is only {glb.Length} bytes");
    }
}
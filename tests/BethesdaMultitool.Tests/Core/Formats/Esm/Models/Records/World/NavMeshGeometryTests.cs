using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models.Records.World;

public sealed class NavMeshGeometryTests
{
    [Fact]
    public void TryParse_DecodesNvvxVerticesAndNvtrTriangleIndices()
    {
        var nvvx = Vvx((1f, 2f, 3f), (4f, 5f, 6f), (7f, 8f, 9f), (10f, 11f, 12f));
        // NVTR records are 16 bytes; only the first three ushorts (vertex indices) are read.
        var nvtr = Tr((0, 1, 2), (2, 1, 3));
        var record = Navm(nvvx, nvtr);

        var geom = NavMeshGeometry.TryParse(record);

        Assert.NotNull(geom);
        Assert.Equal(4, geom!.Vertices.Length);
        Assert.Equal(new Vector3(1f, 2f, 3f), geom.Vertices[0]);
        Assert.Equal(new Vector3(10f, 11f, 12f), geom.Vertices[3]);
        Assert.Equal(2, geom.Triangles.Length);
        Assert.Equal(((ushort)0, (ushort)1, (ushort)2), geom.Triangles[0]);
        Assert.Equal(((ushort)2, (ushort)1, (ushort)3), geom.Triangles[1]);
    }

    [Fact]
    public void TryParse_ReturnsNullWhenSubrecordsMissing()
    {
        Assert.Null(NavMeshGeometry.TryParse(Navm(Vvx((0f, 0f, 0f)), null)));
        Assert.Null(NavMeshGeometry.TryParse(Navm(null, Tr((0, 0, 0)))));
        Assert.Null(NavMeshGeometry.TryParse(new NavMeshRecord()));
    }

    [Fact]
    public void TryParse_ReturnsNullWhenSubrecordsMisSizedOrEmpty()
    {
        // NVVX not a multiple of 12, NVTR not a multiple of 16.
        Assert.Null(NavMeshGeometry.TryParse(Navm(new byte[10], new byte[16])));
        Assert.Null(NavMeshGeometry.TryParse(Navm(new byte[12], new byte[15])));
        // Zero-length both.
        Assert.Null(NavMeshGeometry.TryParse(Navm([], [])));
    }

    [Fact]
    public void TryParse_DecodesSkyrimNvnmInteriorGeometry()
    {
        // Skyrim interior NVNM: parentWorld == 0, union = parent cell FormID. The geometry offsets
        // (vertex count at 16, vertex array at 20) are independent of the union content.
        var nvnm = Nvnm(0u, 0x00027D1Cu,
            [(1f, 2f, 3f), (4f, 5f, 6f), (7f, 8f, 9f)], [(0, 1, 2)]);

        var geom = NavMeshGeometry.TryParse(NavmNvnm(nvnm));

        Assert.NotNull(geom);
        Assert.Equal(3, geom!.Vertices.Length);
        Assert.Equal(new Vector3(7f, 8f, 9f), geom.Vertices[2]);
        Assert.Equal(((ushort)0, (ushort)1, (ushort)2), Assert.Single(geom.Triangles));
    }

    [Fact]
    public void TryParse_SkyrimNvnmExterior_DecodesSameGeometryDespiteGridUnion()
    {
        // Exterior NVNM: parentWorld != 0, union = packed {GridY, GridX} (4 bytes). The geometry must
        // decode identically — this guards against the union being misread as sequential fields (which
        // would shift every exterior navmesh's vertices by 4 bytes).
        var nvnm = Nvnm(0x0000003Cu, 0xFFFE0001u,
            [(0f, 0f, 0f), (1f, 1f, 1f), (2f, 2f, 2f), (3f, 3f, 3f)], [(0, 1, 2), (1, 2, 3)]);

        var geom = NavMeshGeometry.TryParse(NavmNvnm(nvnm));

        Assert.NotNull(geom);
        Assert.Equal(4, geom!.Vertices.Length);
        Assert.Equal(2, geom.Triangles.Length);
        Assert.Equal(((ushort)1, (ushort)2, (ushort)3), geom.Triangles[1]);
    }

    [Fact]
    public void TryParse_Pgrd_SynthesizesNodeMarkersAndLinkRibbons()
    {
        // Three points; pt0↔pt1 and pt1↔pt2 linked (each link appears once per endpoint in PGRR,
        // TES4 convention). Expect 3 node diamonds (2 tris each) + 2 link ribbons (2 tris each).
        var pgrp = Pgrp(
            ((0f, 0f, 100f), 1),
            ((512f, 0f, 110f), 2),
            ((512f, 512f, 120f), 1));
        var pgrr = Pgrr(1, 0, 2, 1);

        var geom = NavMeshGeometry.TryParse(Pgrd(pgrp, pgrr));

        Assert.NotNull(geom);
        Assert.Equal(20, geom!.Vertices.Length); // 5 quads × 4 verts
        Assert.Equal(10, geom.Triangles.Length); // 5 quads × 2 tris
        // Node marker sits at the authored position (± halfSize, small z lift).
        Assert.Equal(100f + 6f, geom.Vertices[0].Z);
        Assert.Equal(-16f, geom.Vertices[0].X);
    }

    [Fact]
    public void TryParse_Pgrd_SkipsExternalAndMalformedLinkTargets()
    {
        // pt0 links to −1 (inter-cell marker) and 99 (out of range) — only the node markers and the
        // valid pt0↔pt1 link must survive.
        var pgrp = Pgrp(
            ((0f, 0f, 0f), 3),
            ((256f, 0f, 0f), 1));
        var pgrr = Pgrr(-1, 99, 1, 0);

        var geom = NavMeshGeometry.TryParse(Pgrd(pgrp, pgrr));

        Assert.NotNull(geom);
        Assert.Equal(12, geom!.Vertices.Length); // 2 nodes + 1 ribbon
        Assert.Equal(6, geom.Triangles.Length);
    }

    [Fact]
    public void TryParse_Pgrd_WithoutPgrrStillEmitsNodeMarkers()
    {
        var geom = NavMeshGeometry.TryParse(Pgrd(Pgrp(((0f, 0f, 0f), 0)), null));

        Assert.NotNull(geom);
        Assert.Equal(4, geom!.Vertices.Length);
        Assert.Equal(2, geom.Triangles.Length);
    }

    [Fact]
    public void TryParse_Pgrd_MisSizedPgrpReturnsNull()
    {
        Assert.Null(NavMeshGeometry.TryParse(Pgrd(new byte[15], null)));
        Assert.Null(NavMeshGeometry.TryParse(Pgrd([], null)));
    }

    [Fact]
    public void TryParse_NvnmTruncatedOrEmpty_ReturnsNull()
    {
        Assert.Null(NavMeshGeometry.TryParse(NavmNvnm(new byte[19]))); // shorter than the 20-byte header

        // Declares 5 vertices but carries none → the triangle-count offset runs past the blob.
        var truncated = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(16, 4), 5u);
        Assert.Null(NavMeshGeometry.TryParse(NavmNvnm(truncated)));
    }

    private static NavMeshRecord Navm(byte[]? nvvx, byte[]? nvtr)
    {
        var subs = new List<NavMeshSubrecord>();
        if (nvvx is not null) subs.Add(new NavMeshSubrecord("NVVX", nvvx));
        if (nvtr is not null) subs.Add(new NavMeshSubrecord("NVTR", nvtr));
        return new NavMeshRecord { FormId = 0x1234, RawSubrecords = subs };
    }

    private static NavMeshRecord NavmNvnm(byte[] nvnm)
    {
        return new NavMeshRecord { FormId = 0x5678, RawSubrecords = [new NavMeshSubrecord("NVNM", nvnm)] };
    }

    private static byte[] Nvnm(
        uint parentWorld, uint unionValue, (float X, float Y, float Z)[] verts, (ushort A, ushort B, ushort C)[] tris)
    {
        var b = new byte[20 + verts.Length * 12 + 4 + tris.Length * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), 12u); // version
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), 0xABCDu); // CRC hash
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8, 4), parentWorld); // parent world (0 = interior)
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12, 4), unionValue); // union: cell formid OR packed grid
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16, 4), (uint)verts.Length);

        var p = 20;
        foreach (var v in verts)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p, 4), v.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p + 4, 4), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p + 8, 4), v.Z);
            p += 12;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p, 4), (uint)tris.Length);
        p += 4;
        foreach (var t in tris)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p, 2), t.A);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 2, 2), t.B);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 4, 2), t.C);
            p += 16; // remaining 10 bytes (edge/cover flags) left zero
        }

        return b;
    }

    private static NavMeshRecord Pgrd(byte[] pgrp, byte[]? pgrr)
    {
        var subs = new List<NavMeshSubrecord> { new("PGRP", pgrp) };
        if (pgrr is not null) subs.Add(new NavMeshSubrecord("PGRR", pgrr));
        return new NavMeshRecord { FormId = 0x9ABC, RawSubrecords = subs };
    }

    private static byte[] Pgrp(params ((float X, float Y, float Z) Pos, byte Connections)[] points)
    {
        var bytes = new byte[points.Length * 16];
        for (var i = 0; i < points.Length; i++)
        {
            var off = i * 16;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off, 4), points[i].Pos.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 4, 4), points[i].Pos.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 8, 4), points[i].Pos.Z);
            bytes[off + 12] = points[i].Connections;
        }

        return bytes;
    }

    private static byte[] Pgrr(params short[] targets)
    {
        var bytes = new byte[targets.Length * 2];
        for (var i = 0; i < targets.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), targets[i]);
        }

        return bytes;
    }

    private static byte[] Vvx(params (float X, float Y, float Z)[] verts)
    {
        var bytes = new byte[verts.Length * 12];
        for (var i = 0; i < verts.Length; i++)
        {
            var off = i * 12;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off, 4), verts[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 4, 4), verts[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off + 8, 4), verts[i].Z);
        }

        return bytes;
    }

    private static byte[] Tr(params (ushort A, ushort B, ushort C)[] tris)
    {
        var bytes = new byte[tris.Length * 16];
        for (var i = 0; i < tris.Length; i++)
        {
            var off = i * 16;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(off, 2), tris[i].A);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(off + 2, 2), tris[i].B);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(off + 4, 2), tris[i].C);
            // Remaining 10 bytes (edge/cover flags) left zero — ignored by the parser.
        }

        return bytes;
    }
}
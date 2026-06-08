using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Models.Records.World;

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
        Assert.Equal(new System.Numerics.Vector3(1f, 2f, 3f), geom.Vertices[0]);
        Assert.Equal(new System.Numerics.Vector3(10f, 11f, 12f), geom.Vertices[3]);
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

    private static NavMeshRecord Navm(byte[]? nvvx, byte[]? nvtr)
    {
        var subs = new List<NavMeshSubrecord>();
        if (nvvx is not null) subs.Add(new NavMeshSubrecord("NVVX", nvvx));
        if (nvtr is not null) subs.Add(new NavMeshSubrecord("NVTR", nvtr));
        return new NavMeshRecord { FormId = 0x1234, RawSubrecords = subs };
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

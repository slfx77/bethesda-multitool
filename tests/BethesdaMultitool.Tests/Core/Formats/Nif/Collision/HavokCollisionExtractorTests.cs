using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Collision;

/// <summary>
///     Covers <see cref="HavokCollisionExtractor" />: decoding a hand-packed
///     <c>bhkCollisionObject → bhkRigidBody[T] → shape tree → hkPackedNiTriStripsData</c> chain into a
///     triangle soup. Walk mode rides this gapless physics mesh instead of the gappy visual mesh.
///     Vertices come out in world units (Havok units ×7, plus the shape's own scale and any
///     bhkRigidBodyT transform); triangle indices are preserved (weld info dropped).
/// </summary>
public sealed class HavokCollisionExtractorTests
{
    private const float Tol = 1e-2f;

    private static readonly int[] SingleTriangleIndices = [0, 1, 2];
    private static readonly int[] RebasedTriangleIndices = [0, 1, 2, 3, 4, 5];

    [Fact]
    public void TryExtract_UncompressedPackedTriStrips_ScalesBySevenAndPreservesIndices()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false), // target out of range → identity transform
            RigidBody(2, false),
            Mopp(3, 1f, false),
            PackedShape(4, Vector3.One, false),
            PackedData(
                [new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)],
                [(0, 1, 2)],
                false,
                false));

        var soup = HavokCollisionExtractor.TryExtract(data, nif, false);

        Assert.True(soup.HasValue);
        Assert.Equal(SingleTriangleIndices, soup!.Value.Triangles);
        AssertVec(new Vector3(7, 0, 0), soup.Value.Positions[0]);
        AssertVec(new Vector3(0, 7, 0), soup.Value.Positions[1]);
        AssertVec(new Vector3(0, 0, 7), soup.Value.Positions[2]);
    }

    [Fact]
    public void TryExtract_ShapeScale_MultipliesOnTopOfHavokScale()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            PackedShape(3, new Vector3(2, 2, 2), false), // ×2 on top of ×7 = ×14
            PackedData([new Vector3(1, 0, 0)], [(0, 0, 0)], false, false));

        var soup = HavokCollisionExtractor.TryExtract(data, nif, false);

        Assert.True(soup.HasValue);
        AssertVec(new Vector3(14, 0, 0), soup!.Value.Positions[0]);
    }

    [Fact]
    public void TryExtract_BigEndianCompressedVertices_DecodesHalfFloats()
    {
        var (data, nif) = BuildNif(
            true,
            CollisionObject(99, 1, true),
            RigidBody(2, true),
            PackedShape(3, Vector3.One, true),
            PackedData(
                [new Vector3(1, 0, 0), new Vector3(0, 2, 0)],
                [(0, 1, 0)],
                true,
                true));

        var soup = HavokCollisionExtractor.TryExtract(data, nif, true);

        Assert.True(soup.HasValue);
        // Half-float precision → looser tolerance.
        Assert.Equal(7f, soup!.Value.Positions[0].X, 0.05f);
        Assert.Equal(14f, soup.Value.Positions[1].Y, 0.05f);
    }

    [Fact]
    public void TryExtract_ListShape_ConcatenatesSubShapesWithRebasedIndices()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            ListShape([3, 5], false),
            PackedShape(4, Vector3.One, false),
            PackedData([new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)], [(0, 1, 2)],
                false, false),
            PackedShape(6, Vector3.One, false),
            PackedData([new Vector3(2, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 2)], [(0, 1, 2)],
                false, false));

        var soup = HavokCollisionExtractor.TryExtract(data, nif, false);

        Assert.True(soup.HasValue);
        Assert.Equal(6, soup!.Value.Positions.Length);
        // Second sub-shape's indices must be re-based by the first's vertex count (3).
        Assert.Equal(RebasedTriangleIndices, soup.Value.Triangles);
        AssertVec(new Vector3(14, 0, 0), soup.Value.Positions[3]);
    }

    [Fact]
    public void TryExtract_RigidBodyT_AppliesTranslation()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBodyT(2, new Vector3(10, 0, 0), Quaternion.Identity, false),
            PackedShape(3, Vector3.One, false),
            PackedData([new Vector3(1, 0, 0)], [(0, 0, 0)], false, false));

        var soup = HavokCollisionExtractor.TryExtract(data, nif, false);

        Assert.True(soup.HasValue);
        // Vertex ×7 = (7,0,0); translation ×7 = (70,0,0); rotate-then-translate → (77,0,0).
        AssertVec(new Vector3(77, 0, 0), soup!.Value.Positions[0]);
    }

    [Fact]
    public void TryExtract_NoCollisionObject_ReturnsNull()
    {
        var (data, nif) = BuildNif(false, ("NiAlphaProperty", new byte[16]));
        Assert.Null(HavokCollisionExtractor.TryExtract(data, nif, false));
    }

    [Fact]
    public void TryExtract_TruncatedPackedData_ReturnsNullWithoutThrowing()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            PackedShape(3, Vector3.One, false),
            // Claims one triangle but the block ends right after the count.
            ("hkPackedNiTriStripsData", TruncatedPackedDataPayload(false)));

        Assert.Null(HavokCollisionExtractor.TryExtract(data, nif, false));
    }

    // ---- buffer builders --------------------------------------------------------------------

    private static void AssertVec(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, Tol);
        Assert.Equal(expected.Y, actual.Y, Tol);
        Assert.Equal(expected.Z, actual.Z, Tol);
    }

    private static (byte[] data, NifInfo nif) BuildNif(bool bigEndian, params (string type, byte[] payload)[] blocks)
    {
        var nif = new NifInfo { IsBigEndian = bigEndian, BlockCount = blocks.Length };
        using var ms = new MemoryStream();
        var offsets = new int[blocks.Length];
        for (var i = 0; i < blocks.Length; i++)
        {
            offsets[i] = (int)ms.Length;
            ms.Write(blocks[i].payload);
        }

        var data = ms.ToArray();
        for (var i = 0; i < blocks.Length; i++)
        {
            nif.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeName = blocks[i].type,
                DataOffset = offsets[i],
                Size = blocks[i].payload.Length
            });
        }

        return (data, nif);
    }

    private static (string, byte[]) CollisionObject(int target, int body, bool be)
    {
        var b = new byte[10];
        WriteI32(b, 0, target, be);
        WriteU16(b, 4, 0, be);
        WriteI32(b, 6, body, be);
        return ("bhkCollisionObject", b);
    }

    private static (string, byte[]) RigidBody(int shape, bool be)
    {
        var b = new byte[4];
        WriteI32(b, 0, shape, be);
        return ("bhkRigidBody", b);
    }

    private static (string, byte[]) RigidBodyT(int shape, Vector3 translation, Quaternion rotation, bool be)
    {
        var b = new byte[60];
        WriteI32(b, 0, shape, be);
        WriteF(b, 28, translation.X, be);
        WriteF(b, 32, translation.Y, be);
        WriteF(b, 36, translation.Z, be);
        WriteF(b, 44, rotation.X, be);
        WriteF(b, 48, rotation.Y, be);
        WriteF(b, 52, rotation.Z, be);
        WriteF(b, 56, rotation.W, be);
        return ("bhkRigidBodyT", b);
    }

    private static (string, byte[]) Mopp(int childShape, float scale, bool be)
    {
        var b = new byte[20];
        WriteI32(b, 0, childShape, be);
        WriteF(b, 16, scale, be);
        return ("bhkMoppBvTreeShape", b);
    }

    private static (string, byte[]) PackedShape(int dataRef, Vector3 scale, bool be)
    {
        var b = new byte[56];
        WriteF(b, 16, scale.X, be);
        WriteF(b, 20, scale.Y, be);
        WriteF(b, 24, scale.Z, be);
        WriteF(b, 28, 0f, be);
        WriteI32(b, 52, dataRef, be);
        return ("bhkPackedNiTriStripsShape", b);
    }

    private static (string, byte[]) ListShape(int[] subRefs, bool be)
    {
        var b = new byte[4 + subRefs.Length * 4];
        WriteU32(b, 0, (uint)subRefs.Length, be);
        for (var i = 0; i < subRefs.Length; i++) WriteI32(b, 4 + i * 4, subRefs[i], be);
        return ("bhkListShape", b);
    }

    private static (string, byte[]) PackedData(
        Vector3[] vertices, (ushort a, ushort b, ushort c)[] triangles, bool compressed, bool be)
    {
        var vbytes = compressed ? 6 : 12;
        var buffer = new byte[4 + triangles.Length * 8 + 4 + 1 + vertices.Length * vbytes];
        var p = 0;
        WriteU32(buffer, p, (uint)triangles.Length, be);
        p += 4;
        foreach (var (a, bb, c) in triangles)
        {
            WriteU16(buffer, p, a, be);
            WriteU16(buffer, p + 2, bb, be);
            WriteU16(buffer, p + 4, c, be);
            WriteU16(buffer, p + 6, 0, be); // weld info (dropped on read)
            p += 8;
        }

        WriteU32(buffer, p, (uint)vertices.Length, be);
        p += 4;
        buffer[p] = (byte)(compressed ? 1 : 0);
        p += 1;
        foreach (var v in vertices)
        {
            if (compressed)
            {
                WriteHalf(buffer, p, v.X, be);
                WriteHalf(buffer, p + 2, v.Y, be);
                WriteHalf(buffer, p + 4, v.Z, be);
                p += 6;
            }
            else
            {
                WriteF(buffer, p, v.X, be);
                WriteF(buffer, p + 4, v.Y, be);
                WriteF(buffer, p + 8, v.Z, be);
                p += 12;
            }
        }

        return ("hkPackedNiTriStripsData", buffer);
    }

    private static byte[] TruncatedPackedDataPayload(bool be)
    {
        var b = new byte[4];
        WriteU32(b, 0, 1u, be); // numTriangles=1 with no following data
        return b;
    }

    private static void WriteI32(byte[] b, int o, int v, bool be)
    {
        if (be) BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o), v);
        else BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
    }

    private static void WriteU32(byte[] b, int o, uint v, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
        else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    }

    private static void WriteU16(byte[] b, int o, ushort v, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v);
        else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    }

    private static void WriteF(byte[] b, int o, float v, bool be)
    {
        WriteU32(b, o, BitConverter.SingleToUInt32Bits(v), be);
    }

    private static void WriteHalf(byte[] b, int o, float v, bool be)
    {
        WriteU16(b, o, BitConverter.HalfToUInt16Bits((Half)v), be);
    }
}

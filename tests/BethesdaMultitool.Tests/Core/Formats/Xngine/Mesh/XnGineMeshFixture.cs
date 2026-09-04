using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine.Mesh;

/// <summary>
///     Synthetic XnGine mesh records in the retail layout: 64-byte header, then (in this builder's
///     order) points, normals, plane list and 24-byte plane data. v2.5 stores point offsets
///     divided by three.
/// </summary>
internal static class XnGineMeshFixture
{
    public sealed record Plane(ushort TextureBits, IReadOnlyList<(int PointIndex, short U, short V)> Points, (int X, int Y, int Z) Normal);

    public static ushort Texture(int archive, int record)
    {
        return (ushort)((archive << 7) | (record & 0x7F));
    }

    public static byte[] Build(string version, IReadOnlyList<(int X, int Y, int Z)> points, IReadOnlyList<Plane> planes, uint radius = 1000)
    {
        var pointList = new byte[points.Count * 12];
        for (var i = 0; i < points.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(pointList.AsSpan(i * 12), points[i].X);
            BinaryPrimitives.WriteInt32LittleEndian(pointList.AsSpan(i * 12 + 4), points[i].Y);
            BinaryPrimitives.WriteInt32LittleEndian(pointList.AsSpan(i * 12 + 8), points[i].Z);
        }

        var normals = new byte[planes.Count * 12];
        var planeList = new List<byte>();
        for (var k = 0; k < planes.Count; k++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(normals.AsSpan(k * 12), planes[k].Normal.X);
            BinaryPrimitives.WriteInt32LittleEndian(normals.AsSpan(k * 12 + 4), planes[k].Normal.Y);
            BinaryPrimitives.WriteInt32LittleEndian(normals.AsSpan(k * 12 + 8), planes[k].Normal.Z);

            planeList.Add((byte)planes[k].Points.Count);
            planeList.Add(0x11);
            AddUInt16(planeList, planes[k].TextureBits);
            AddUInt32(planeList, 0xCAFEBABE);
            foreach (var (pointIndex, u, v) in planes[k].Points)
            {
                var byteOffset = pointIndex * 12;
                AddInt32(planeList, version == "v2.5" ? byteOffset / 3 : byteOffset);
                AddInt16(planeList, u);
                AddInt16(planeList, v);
            }
        }

        var planeData = new byte[planes.Count * 24];
        for (var i = 0; i < planeData.Length; i++)
        {
            planeData[i] = (byte)(i & 0xFF);
        }

        var pointListOffset = 64;
        var normalListOffset = pointListOffset + pointList.Length;
        var planeListOffset = normalListOffset + normals.Length;
        var planeDataOffset = planeListOffset + planeList.Count;

        var header = new byte[64];
        Encoding.ASCII.GetBytes(version).CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), points.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), planes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), radius);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), planeDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), planeDataOffset + planeData.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), 24832);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48), pointListOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(52), normalListOffset);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60), planeListOffset);

        return [.. header, .. pointList, .. normals, .. planeList, .. planeData];
    }

    /// <summary>A unit quad in the XZ plane (Y-down space), 256 native units on a side, textured 64x64 texels.</summary>
    public static byte[] Quad(string version = "v2.7", uint objectId = 5000)
    {
        _ = objectId;
        return Build(version,
            [(0, 0, 0), (256, 0, 0), (256, 0, 256), (0, 0, 256)],
            [new Plane(Texture(24, 3), [(0, 0, 0), (1, 1024, 0), (2, 0, 1024), (3, 0, 0)], (0, -256, 0))]);
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value & 0xFF));
        bytes.Add((byte)(value >> 8));
    }

    private static void AddInt16(List<byte> bytes, short value)
    {
        AddUInt16(bytes, (ushort)value);
    }

    private static void AddUInt32(List<byte> bytes, uint value)
    {
        for (var shift = 0; shift < 32; shift += 8)
        {
            bytes.Add((byte)((value >> shift) & 0xFF));
        }
    }

    private static void AddInt32(List<byte> bytes, int value)
    {
        AddUInt32(bytes, (uint)value);
    }
}

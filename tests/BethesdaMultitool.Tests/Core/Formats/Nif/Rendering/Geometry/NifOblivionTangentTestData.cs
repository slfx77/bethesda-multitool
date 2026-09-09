using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

internal sealed class NifOblivionTangentTestData
{
    internal const string ExtraName = "Tangent space (binormal & tangent vectors)";
    internal const int ShapeCountOffset = 10; // SizedString("Target")
    internal const int ShapeFirstLinkOffset = 14;
    internal static readonly Vector3[] Tangents = [new(2, 0, 0), new(-3, 0, 0), new(1, 1, 0)];
    internal static readonly Vector3[] Bitangents = [new(0, 4, 0), new(0, 5, 0), new(-2, 2, 0)];
    internal static readonly Vector3[] Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
    internal static readonly float[] Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1];

    internal NifOblivionTangentTestData(bool strips = false, bool inline = false, int[]? links = null)
    {
        Info = new NifInfo
        {
            HeaderString = "Gamebryo File Format, Version 20.0.0.5",
            BinaryVersion = 0x14000005,
            UserVersion = 11,
            BsVersion = 11,
            HasInlineStrings = true,
            BlockCount = 3
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        var attachedLinks = links ?? [1];
        AddBlock(strips ? "NiTriStrips" : "NiTriShape", () =>
        {
            WriteString(writer, "Target");
            writer.Write((uint)attachedLinks.Length);
            foreach (var link in attachedLinks)
                writer.Write(link);
        });
        AddBlock("NiBinaryExtraData", () =>
        {
            WriteString(writer, ExtraName);
            writer.Write(72u);
            foreach (var vector in Bitangents)
                WriteVector(writer, vector);
            foreach (var vector in Tangents)
                WriteVector(writer, vector);
        });
        AddBlock(strips ? "NiTriStripsData" : "NiTriShapeData", () => WriteGeometry(writer, strips, inline));
        writer.Flush();
        Data = stream.ToArray();

        void AddBlock(string type, Action write)
        {
            var start = checked((int)stream.Position);
            write();
            Info.Blocks.Add(new BlockInfo
            {
                Index = Info.Blocks.Count,
                TypeName = type,
                DataOffset = start,
                Size = checked((int)stream.Position) - start
            });
        }
    }

    internal byte[] Data { get; }
    internal NifInfo Info { get; }
    internal int ExtraPayloadOffset => Info.Blocks[1].DataOffset + 8 + ExtraName.Length;

    internal static Vector3 VectorAt(float[] values, int vertex)
    {
        return new Vector3(values[vertex * 3], values[vertex * 3 + 1], values[vertex * 3 + 2]);
    }

    private static void WriteGeometry(BinaryWriter writer, bool strips, bool inline)
    {
        writer.Write(0u); // Group ID
        writer.Write((ushort)3);
        writer.Write((ushort)0); // Keep/Compress bytes
        writer.Write((byte)1); // Has Vertices
        foreach (var position in Positions)
            WriteVector(writer, position);
        writer.Write(inline ? (ushort)0x1001 : (ushort)1); // one UV set
        writer.Write((byte)1); // Has Normals
        for (var i = 0; i < 3; i++)
            WriteVector(writer, Vector3.UnitZ);
        if (inline)
        {
            for (var i = 0; i < 3; i++)
                WriteVector(writer, Vector3.UnitY);
            for (var i = 0; i < 3; i++)
                WriteVector(writer, -Vector3.UnitX);
        }

        WriteVector(writer, Vector3.Zero);
        writer.Write(2f); // NiBound radius
        writer.Write((byte)0); // Has Vertex Colors
        foreach (var position in Positions)
        {
            writer.Write(position.X);
            writer.Write(position.Y);
        }

        writer.Write((ushort)0); // Consistency Flags
        writer.Write(-1); // Additional Data ref
        writer.Write((ushort)1); // triangle count
        if (strips)
        {
            writer.Write((ushort)1); // strip count
            writer.Write((ushort)3); // strip length
        }
        else
        {
            writer.Write(3u); // triangle point count
        }

        writer.Write((byte)1); // Has Points / Has Triangles
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }
}
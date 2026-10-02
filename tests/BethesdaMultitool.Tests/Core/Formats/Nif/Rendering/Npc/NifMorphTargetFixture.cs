using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

/// <summary>Writes two independent LE skinned shapes in source order, with the matching head second.</summary>
internal static class NifMorphTargetFixture
{
    private static readonly string[] Names = ["Root", "Attachment", "Head", "MorphBone"];
    internal const int AttachmentShape = 1;
    internal const int HeadShape = 3;
    internal const int HeadData = 4;

    /// <summary>Writes a complete tiny 20.2.0.7/BS34 NIF without a production writer or corpus payload.</summary>
    internal static byte[] Create(bool ambiguous = false)
    {
        byte[][] blocks =
        [
            Block(writer => Node(writer, 0, [AttachmentShape, HeadShape, 5])),
            Block(writer => Shape(writer, 1, 2, 6)),
            Block(writer => Geometry(writer, 10, !ambiguous)),
            Block(writer => Shape(writer, 2, HeadData, 7)),
            Block(writer => Geometry(writer, 1, false)),
            Block(writer => Node(writer, 3, [])),
            Block(SkinInstance),
            Block(SkinInstance),
            Block(SkinData),
            Block(Shader)
        ];
        string[] types = ["NiNode", "NiTriShape", "NiTriShapeData", "NiSkinInstance", "NiSkinData",
            "BSShaderNoLightingProperty"];
        ushort[] indices = [0, 1, 2, 1, 2, 0, 3, 3, 4, 5];
        return Block(writer =>
        {
            writer.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
            writer.Write(0x14020007u);
            writer.Write((byte)1);
            writer.Write(11u);
            writer.Write((uint)blocks.Length);
            writer.Write(34u);
            for (var index = 0; index < 3; index++)
            {
                writer.Write((byte)1);
                writer.Write((byte)0);
            }
            writer.Write((ushort)types.Length);
            foreach (var type in types) { String(writer, type); }
            foreach (var index in indices) { writer.Write(index); }
            foreach (var block in blocks) { writer.Write((uint)block.Length); }
            writer.Write(4u);
            writer.Write(10u);
            foreach (var name in Names) { String(writer, name); }
            writer.Write(0u);
            foreach (var block in blocks) { writer.Write(block); }
            writer.Write(1u);
            writer.Write(0);
        });
    }

    /// <summary>Owns one small independent encoded block.</summary>
    private static byte[] Block(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Writes an ASCII uint32-sized source string.</summary>
    private static void String(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    /// <summary>Writes the fixed source NiObjectNET prefix.</summary>
    private static void Object(BinaryWriter writer, int name)
    {
        writer.Write(name);
        writer.Write(0u);
        writer.Write(-1);
    }

    /// <summary>Writes a visible identity NiAVObject, optionally with the shared shader property.</summary>
    private static void AvObject(BinaryWriter writer, bool shader)
    {
        writer.Write(0x0008000Eu);
        writer.Write(0f); writer.Write(0f); writer.Write(0f);
        Rotation(writer);
        writer.Write(1f);
        writer.Write(shader ? 1u : 0u);
        if (shader) { writer.Write(9); }
        writer.Write(-1);
    }

    /// <summary>Writes a source identity matrix in row order.</summary>
    private static void Rotation(BinaryWriter writer)
    {
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                writer.Write(row == column ? 1f : 0f);
            }
        }
    }

    /// <summary>Writes a root or bone with its explicit children.</summary>
    private static void Node(BinaryWriter writer, int name, ReadOnlySpan<int> children)
    {
        Object(writer, name);
        AvObject(writer, false);
        writer.Write((uint)children.Length);
        foreach (var child in children) { writer.Write(child); }
        writer.Write(0u);
    }

    /// <summary>Writes a shape with its exact data and skin owner indices.</summary>
    private static void Shape(BinaryWriter writer, int name, int data, int skin)
    {
        Object(writer, name);
        AvObject(writer, true);
        writer.Write(data);
        writer.Write(skin);
        writer.Write(0u);
        writer.Write(-1);
        writer.Write((byte)0);
    }

    /// <summary>Writes three local positions, normals and one oriented triangle; equal counts cannot distinguish the two shapes.</summary>
    private static void Geometry(BinaryWriter writer, float x, bool reversed)
    {
        writer.Write(0);
        writer.Write((ushort)3);
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)1);
        float[] positions = [x, 0, 0, x + 1, 0, 0, x, 1, 0];
        foreach (var value in positions) { writer.Write(value); }
        writer.Write((ushort)0);
        writer.Write((byte)1);
        for (var index = 0; index < 3; index++)
        {
            writer.Write(0f); writer.Write(0f); writer.Write(1f);
        }
        writer.Write(x); writer.Write(0f); writer.Write(0f); writer.Write(2f);
        writer.Write((byte)0);
        writer.Write((ushort)0x4000);
        writer.Write(-1);
        writer.Write((ushort)1);
        writer.Write(3u);
        writer.Write((byte)1);
        writer.Write((ushort)0);
        writer.Write((ushort)(reversed ? 2 : 1));
        writer.Write((ushort)(reversed ? 1 : 2));
        writer.Write((ushort)0);
    }

    /// <summary>References the shared identity inverse bind and the named bone explicitly.</summary>
    private static void SkinInstance(BinaryWriter writer)
    {
        writer.Write(8); writer.Write(-1); writer.Write(0); writer.Write(1u); writer.Write(5);
    }

    /// <summary>Writes one bone with unit influence on each of the three source vertices.</summary>
    private static void SkinData(BinaryWriter writer)
    {
        Transform(writer);
        writer.Write(1u);
        writer.Write((byte)1);
        Transform(writer);
        writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(2f);
        writer.Write((ushort)3);
        for (ushort vertex = 0; vertex < 3; vertex++)
        {
            writer.Write(vertex);
            writer.Write(1f);
        }
    }

    /// <summary>Writes one identity NiTransform using its rotation/translation/scale layout.</summary>
    private static void Transform(BinaryWriter writer)
    {
        Rotation(writer);
        writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
    }

    /// <summary>Writes a real renderable shader source property, keeping both shapes out of helper exclusion.</summary>
    private static void Shader(BinaryWriter writer)
    {
        Object(writer, -1);
        writer.Write((ushort)1);
        writer.Write(0u); writer.Write(0x82000000u); writer.Write(1u); writer.Write(1f); writer.Write(3u);
        String(writer, "synthetic.dds");
        writer.Write(1f); writer.Write(0f); writer.Write(1f); writer.Write(0f);
    }
}

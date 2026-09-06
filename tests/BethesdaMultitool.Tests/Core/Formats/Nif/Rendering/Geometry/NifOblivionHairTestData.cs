using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

/// <summary>Independent legacy 10.2 fixture, including PC PS2 descriptor fields and root effects.</summary>
internal sealed class NifOblivionHairTestData
{
    private static readonly int[] PropertyOrder = [7, 6, 5, 3];
    private static readonly int[] ReversedPropertyOrder = [3, 5, 6, 7];
    private static readonly string[] AdditionalTextureSlots = ["dark", "detail", "gloss-map", "glow", "bump", "decal"];

    internal NifOblivionHairTestData(string materialName = "Hair", bool reverseProperties = false)
    {
        Info = new NifInfo
        {
            HeaderString = "Gamebryo File Format, Version 10.2.0.0",
            BinaryVersion = 0x0A020000,
            UserVersion = 10,
            BsVersion = 9,
            HasInlineStrings = true,
            BlockCount = 11
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        AddBlock("NiNode", () =>
        {
            WriteString("Synthetic Hair");
            Mark("root-extras"); writer.Write(1u); writer.Write(1);
            Mark("root-controller"); writer.Write(-1);
            AvTail("root");
            writer.Write(3u); writer.Write(2); writer.Write(9); writer.Write(10);
            Mark("root-effects"); writer.Write(2u);
            Mark("root-first-effect"); writer.Write(9); writer.Write(10);
        });
        AddBlock("NiStringExtraData", () => { WriteString("Prn"); WriteString("Bip01 Head"); });
        AddBlock("NiTriShape", () =>
        {
            WriteString("Synthetic Hair:0"); StaticTail("shape");
            writer.Write((ushort)0x10); Transform();
            Mark("properties"); writer.Write(4u);
            foreach (var reference in reverseProperties ? ReversedPropertyOrder : PropertyOrder)
            {
                writer.Write(reference);
            }
            Mark("collision"); writer.Write(-1);
            writer.Write(8);
            Mark("skin"); writer.Write(-1);
            Mark("shader"); writer.Write((byte)0);
        });
        AddBlock("NiTexturingProperty", () =>
        {
            WriteString(string.Empty); StaticTail("texture");
            Mark("apply"); writer.Write(2u); writer.Write(7u); writer.Write((byte)1); writer.Write(4);
            Mark("clamp"); writer.Write(3u);
            Mark("filter"); writer.Write(2u); writer.Write(0u);
            Mark("ps2-l"); writer.Write((short)0);
            Mark("ps2-k"); writer.Write((short)-100);
            Mark("texture-transform"); writer.Write((byte)0);
            foreach (var slot in AdditionalTextureSlots)
            {
                Mark(slot); writer.Write((byte)0);
            }
            Mark("shader-textures"); writer.Write(0u);
        });
        AddBlock("NiSourceTexture", () =>
        {
            WriteString(string.Empty); StaticTail("source");
            writer.Write((byte)1); WriteString(@"textures\characters\hair\authored.dds");
            writer.Write(-1); writer.Write(6u); writer.Write(1u); writer.Write(3u);
            writer.Write((byte)1); writer.Write((byte)1);
        });
        AddBlock("NiAlphaProperty", () =>
        {
            WriteString(string.Empty); StaticTail("alpha");
            Mark("alpha-flags"); writer.Write((ushort)0x12ED);
            Mark("threshold"); writer.Write((byte)0);
        });
        AddBlock("NiVertexColorProperty", () =>
        {
            WriteString(string.Empty); StaticTail("color"); writer.Write((ushort)0);
            Mark("vertex-mode"); writer.Write(2u); writer.Write(1u);
        });
        AddBlock("NiMaterialProperty", () =>
        {
            WriteString(materialName); StaticTail("material");
            Mark("ambient"); WriteVector(Vector3.One);
            Mark("diffuse"); WriteVector(Vector3.One);
            WriteVector(new Vector3(0.9f));
            Mark("emissive"); WriteVector(Vector3.Zero);
            writer.Write(10f); Mark("material-alpha"); writer.Write(1f);
        });
        AddBlock("NiTriShapeData", () =>
        {
            writer.Write(0u); writer.Write((ushort)3); writer.Write((ushort)0);
            writer.Write((byte)1);
            WriteVector(Vector3.Zero); WriteVector(Vector3.UnitX); WriteVector(Vector3.UnitY);
            writer.Write((ushort)1); writer.Write((byte)1);
            WriteVector(Vector3.UnitZ); WriteVector(Vector3.UnitZ); WriteVector(Vector3.UnitZ);
            WriteVector(Vector3.Zero); writer.Write(2f); writer.Write((byte)1);
            for (var vertex = 0; vertex < 3; vertex++)
            {
                Mark($"red-{vertex}"); writer.Write((vertex + 1) * 0.1f);
                Mark($"green-{vertex}"); writer.Write(1f);
                writer.Write(1f);
                Mark($"alpha-{vertex}"); writer.Write(1f);
            }
            Mark("uv");
            writer.Write(0f); writer.Write(0f); writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write((ushort)0); // No additional-data reference at 10.2.0.0.
            writer.Write((ushort)1); writer.Write(3u); writer.Write((byte)1);
            Mark("index"); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
            writer.Write((ushort)0);
        });
        for (var light = 0; light < 2; light++)
        {
            var label = $"light-{light}";
            AddBlock("NiDirectionalLight", () =>
            {
                WriteString("__MAX_Default_Light"); StaticTail(label); AvTail(label);
                writer.Write((byte)1);
                Mark(label + "-affected"); writer.Write(0u);
                Mark(label + "-dimmer"); writer.Write(1f);
                WriteVector(Vector3.Zero); WriteVector(Vector3.One); WriteVector(Vector3.One);
            });
        }
        writer.Flush();
        Data = stream.ToArray();

        void AddBlock(string type, Action write)
        {
            var start = checked((int)stream.Position);
            write();
            Info.Blocks.Add(new BlockInfo
            {
                Index = Info.Blocks.Count, TypeName = type, DataOffset = start,
                Size = checked((int)stream.Position) - start
            });
        }
        void Mark(string name) => Offsets.Add(name, checked((int)stream.Position));
        void WriteString(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write((uint)bytes.Length); writer.Write(bytes);
        }
        void StaticTail(string label)
        {
            writer.Write(0u); Mark(label + "-controller"); writer.Write(-1);
        }
        void AvTail(string label)
        {
            writer.Write((ushort)0x10); Transform();
            Mark(label + "-properties"); writer.Write(0u); writer.Write(-1);
        }
        void Transform()
        {
            WriteVector(Vector3.Zero); WriteVector(Vector3.UnitX); WriteVector(Vector3.UnitY);
            WriteVector(Vector3.UnitZ); writer.Write(1f);
        }
        void WriteVector(Vector3 value)
        {
            writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
        }
    }

    internal byte[] Data { get; }
    internal NifInfo Info { get; }
    internal Dictionary<string, int> Offsets { get; } = new(StringComparer.Ordinal);
}

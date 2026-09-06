using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

/// <summary>Independent little-endian 20.0.0.4 shape/property/geometry byte fixture.</summary>
internal sealed class NifOblivionBodySkinTestData
{
    private static readonly string[] ExtraTextureSlots = ["dark", "detail", "gloss-map", "glow", "bump", "decal"];

    internal NifOblivionBodySkinTestData(string materialName = "skin", string shapeName = "Hand",
        bool reverseProperties = false)
    {
        Info = new NifInfo
        {
            HeaderString = "Gamebryo File Format, Version 20.0.0.4",
            BinaryVersion = 0x14000004,
            UserVersion = 11,
            BsVersion = 11,
            HasInlineStrings = true,
            BlockCount = 6
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        AddBlock("NiTriShape", () =>
        {
            Mark("shape-name"); WriteString(shapeName);
            Mark("shape-extra-count"); writer.Write(1u);
            Mark("shape-extra"); writer.Write(1);
            Mark("shape-controller"); writer.Write(-1);
            Mark("shape-flags"); writer.Write((ushort)0x0016);
            WriteVector(Vector3.Zero);
            WriteVector(Vector3.UnitX); WriteVector(Vector3.UnitY); WriteVector(Vector3.UnitZ);
            writer.Write(1f);
            Mark("property-count"); writer.Write(2u);
            Mark("property-first"); writer.Write(reverseProperties ? 3 : 2);
            Mark("property-second"); writer.Write(reverseProperties ? 2 : 3);
            Mark("collision"); writer.Write(-1);
            Mark("geometry-ref"); writer.Write(5);
            writer.Write(-1); // Unskinned uses the separately proven same-pixel-program branch.
            Mark("has-shader"); writer.Write((byte)0);
        });
        AddBlock("NiBinaryExtraData", () =>
        {
            WriteString(NifOblivionTangentTestData.ExtraName);
            Mark("tangent-length"); writer.Write(72u);
            Mark("bitangent");
            foreach (var vector in NifOblivionTangentTestData.Bitangents)
            {
                WriteVector(vector);
            }
            Mark("tangent");
            foreach (var vector in NifOblivionTangentTestData.Tangents)
            {
                WriteVector(vector);
            }
        });
        AddBlock("NiMaterialProperty", () =>
        {
            WriteString(materialName);
            StaticTail("material");
            Mark("ambient"); WriteVector(Vector3.One);
            Mark("diffuse"); WriteVector(Vector3.One);
            Mark("specular"); WriteVector(Vector3.One);
            Mark("emissive"); WriteVector(Vector3.Zero);
            Mark("gloss"); writer.Write(25f);
            Mark("alpha"); writer.Write(1f);
        });
        AddBlock("NiTexturingProperty", () =>
        {
            WriteString(string.Empty);
            StaticTail("texturing");
            Mark("apply-mode"); writer.Write(2u);
            Mark("texture-count"); writer.Write(7u);
            Mark("has-base"); writer.Write((byte)1);
            Mark("source-ref"); writer.Write(4);
            Mark("clamp"); writer.Write(3u);
            Mark("filter"); writer.Write(2u);
            Mark("uv-set"); writer.Write(0u);
            Mark("texture-transform"); writer.Write((byte)0);
            foreach (var slot in ExtraTextureSlots)
            {
                Mark(slot); writer.Write((byte)0);
            }
            Mark("shader-textures"); writer.Write(0u);
        });
        AddBlock("NiSourceTexture", () =>
        {
            WriteString(string.Empty);
            StaticTail("source");
            Mark("external"); writer.Write((byte)1);
            WriteString(@"textures\synthetic\body.dds");
            Mark("pixel-data"); writer.Write(-1);
            writer.Write(6u); writer.Write(1u); writer.Write(3u);
            Mark("static"); writer.Write((byte)1);
            Mark("direct"); writer.Write((byte)1);
        });
        AddBlock("NiTriShapeData", () =>
        {
            writer.Write(0u);
            Mark("vertex-count"); writer.Write((ushort)3);
            writer.Write((ushort)0);
            Mark("has-vertices"); writer.Write((byte)1);
            Mark("position");
            foreach (var vector in NifOblivionTangentTestData.Positions)
            {
                WriteVector(vector);
            }
            Mark("data-flags"); writer.Write((ushort)1);
            Mark("has-normals"); writer.Write((byte)1);
            Mark("normal");
            for (var i = 0; i < 3; i++)
            {
                WriteVector(Vector3.UnitZ);
            }
            WriteVector(Vector3.Zero); writer.Write(2f);
            Mark("has-colors"); writer.Write((byte)0);
            Mark("uv");
            foreach (var vector in NifOblivionTangentTestData.Positions)
            {
                writer.Write(vector.X); writer.Write(vector.Y);
            }
            writer.Write((ushort)0x4000);
            Mark("additional-data"); writer.Write(-1);
            writer.Write((ushort)1);
            Mark("triangle-points"); writer.Write(3u);
            writer.Write((byte)1);
            Mark("triangle-index"); writer.Write((ushort)0);
            writer.Write((ushort)1); writer.Write((ushort)2);
            writer.Write((ushort)3); // Independently bounded match-group list.
            Mark("match-group");
            for (ushort i = 0; i < 3; i++)
            {
                writer.Write((ushort)1); writer.Write(i);
            }
        });
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

        void Mark(string name) => Offsets.Add(name, checked((int)stream.Position));
        void WriteString(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
        }

        void StaticTail(string label)
        {
            Mark(label + "-extra-count"); writer.Write(0u);
            Mark(label + "-controller"); writer.Write(-1);
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

using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Complete NiNode/NiGeometry/material/data blocks, not a private descriptor-only fixture.</summary>
internal sealed class NifExportMaterialDescriptorTestData
{
    private static readonly Vector3[] Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
    private static readonly Vector3[] BasisAxes = [Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY];

    internal NifExportMaterialDescriptorTestData(
        bool strips = false,
        bool bigEndian = false,
        uint bsVersion = 11,
        uint binaryVersion = 0x14000005,
        string material = "authored",
        bool multipleOwners = false)
    {
        Info = new NifInfo
        {
            HeaderString = (bsVersion, binaryVersion) switch
            {
                (>= 26, _) => "Gamebryo File Format, Version 20.2.0.7",
                (_, 0x14000004) => "Gamebryo File Format, Version 20.0.0.4",
                _ => "Gamebryo File Format, Version 20.0.0.5"
            },
            BinaryVersion = bsVersion >= 26 ? 0x14020007 : binaryVersion,
            UserVersion = 11,
            BsVersion = bsVersion,
            IsBigEndian = bigEndian,
            HasInlineStrings = bsVersion < 26
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        var properties = material switch
        {
            "absent" => Array.Empty<int>(),
            "out-of-range" => [int.MaxValue],
            "wrong-type" => [2],
            _ when bsVersion >= 34 => [3, 4],
            _ => [3]
        };

        AddBlock("NiNode", () =>
        {
            WriteAvObject(writer, "Root", []);
            WriteInt(writer, multipleOwners ? 3 : 1);
            WriteInt(writer, 1);
            if (multipleOwners)
            {
                WriteInt(writer, 4);
                WriteInt(writer, 6);
            }

            WriteInt(writer, 0); // Num Effects.
        });
        AddBlock(strips ? "NiTriStrips" : "NiTriShape", () => WriteShape(writer, "Primary", properties));
        AddBlock(strips ? "NiTriStripsData" : "NiTriShapeData", () => WriteGeometry(writer, strips));
        AddBlock("NiMaterialProperty", () => WriteMaterial(writer,
            material == "black" ? Vector3.Zero : new Vector3(0.125f, 0.375f, 0.625f),
            new Vector3(0.25f, 0.5f, 0.75f), 24f, 0.625f));
        if (material == "truncated")
        {
            // Required Alpha is missing its final byte. Following blocks must not supply it.
            Info.Blocks[3].Size--;
        }

        if (multipleOwners)
        {
            AddBlock(strips ? "NiTriStrips" : "NiTriShape", () => WriteShape(writer, "Second", [5]));
            AddBlock("NiMaterialProperty", () => WriteMaterial(writer,
                new Vector3(0.75f, 0.25f, 0.5f), new Vector3(0.875f, 0.625f, 0.375f), 32f, 0.75f, "skin"));
            AddBlock(strips ? "NiTriStrips" : "NiTriShape", () => WriteShape(writer, "NoMaterial", []));
        }
        else if (bsVersion >= 34)
        {
            // Valid legacy texture-source property avoids the modern helper-shape exclusion.
            // Seven texture slots are present but all unbound; no texture archive is required.
            AddBlock("NiTexturingProperty", () =>
            {
                WriteObjectNet(writer, "UnboundTextures");
                WriteUShort(writer, 0); // TexturingFlags for 20.2.0.7.
                WriteInt(writer, 7);
                for (var slot = 0; slot < 7; slot++)
                {
                    writer.Write((byte)0);
                }

                WriteInt(writer, 0); // Num Shader Textures.
            });
        }

        AddBlock("NiMaterialProperty", () => WriteMaterial(writer,
            new Vector3(0.9375f, 0.8125f, 0.6875f), new Vector3(8, 9, 10), 64f, 0.25f, "DetachedSkin"));
        writer.Flush();
        Data = stream.ToArray();
        Info.BlockCount = Info.Blocks.Count;

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

    private void WriteObjectNet(BinaryWriter writer, string name)
    {
        if (Info.HasInlineStrings)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            WriteInt(writer, bytes.Length);
            writer.Write(bytes);
            // The real legacy measure pass fills this cache; consumers do not reread inline names.
            Info.BlockNames[Info.Blocks.Count] = name;
        }
        else
        {
            WriteInt(writer, Info.Strings.Count);
            Info.Strings.Add(name);
        }

        WriteInt(writer, 0); // Num Extra Data List.
        WriteInt(writer, -1); // Controller.
    }

    private void WriteAvObject(BinaryWriter writer, string name, int[] properties)
    {
        WriteObjectNet(writer, name);
        if (Info.BsVersion > 26)
        {
            WriteInt(writer, 0); // Flags.
        }
        else
        {
            WriteUShort(writer, 0);
        }

        WriteVector(writer, Vector3.Zero); // Translation.
        WriteVector(writer, Vector3.UnitX); // Identity Matrix33 rows.
        WriteVector(writer, Vector3.UnitY);
        WriteVector(writer, Vector3.UnitZ);
        WriteFloat(writer, 1f); // Scale.
        WriteInt(writer, properties.Length);
        foreach (var property in properties)
        {
            WriteInt(writer, property);
        }

        WriteInt(writer, -1); // Collision Object.
    }

    private void WriteShape(BinaryWriter writer, string name, int[] properties)
    {
        WriteAvObject(writer, name, properties);
        WriteInt(writer, 2); // Shared geometry data.
        WriteInt(writer, -1); // Skin Instance.
        if (Info.BinaryVersion >= 0x14020005)
        {
            WriteInt(writer, 0); // Num Materials.
            WriteInt(writer, -1); // Active Material.
            writer.Write((byte)0); // Material Needs Update.
        }
        else
        {
            writer.Write((byte)0); // Has Shader.
        }
    }

    private void WriteMaterial(BinaryWriter writer, Vector3 diffuse, Vector3 specular, float gloss, float alpha,
        string name = "AuthoredMaterial")
    {
        WriteObjectNet(writer, name);
        if (Info.BsVersion < 26)
        {
            WriteVector(writer, new Vector3(0.0625f, 0.1875f, 0.3125f)); // Distinct ambient sentinel.
            WriteVector(writer, diffuse);
        }

        WriteVector(writer, specular);
        WriteVector(writer, new Vector3(0.875f, 0.6875f, 0.9375f)); // Distinct emissive sentinel.
        WriteFloat(writer, gloss);
        WriteFloat(writer, alpha);
        if (Info.BsVersion > 21)
        {
            WriteFloat(writer, 2f); // Emissive Mult.
        }
    }

    private void WriteGeometry(BinaryWriter writer, bool strips)
    {
        WriteInt(writer, 0); // Group ID.
        WriteUShort(writer, 3); // Num Vertices.
        WriteUShort(writer, 0); // Keep and Compress bytes.
        writer.Write((byte)1); // Has Vertices.
        foreach (var position in Positions)
        {
            WriteVector(writer, position);
        }

        WriteUShort(writer, 0x1001); // One UV set and authored inline T/B.
        writer.Write((byte)1); // Has Normals.
        foreach (var axis in BasisAxes)
        {
            for (var vertex = 0; vertex < 3; vertex++)
            {
                WriteVector(writer, axis);
            }
        }

        WriteVector(writer, Vector3.Zero);
        WriteFloat(writer, 2f); // Bounding sphere.
        writer.Write((byte)0); // Has Vertex Colors.
        foreach (var position in Positions)
        {
            WriteFloat(writer, position.X);
            WriteFloat(writer, position.Y);
        }

        WriteUShort(writer, 0); // Consistency Flags.
        WriteInt(writer, -1); // Additional Data.
        WriteUShort(writer, 1); // Num Triangles.
        if (strips)
        {
            WriteUShort(writer, 1); // Num Strips.
            WriteUShort(writer, 3); // Strip Length.
        }
        else
        {
            WriteInt(writer, 3); // Num Triangle Points.
        }

        writer.Write((byte)1); // Has Points / Has Triangles.
        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 2);
        if (!strips)
        {
            WriteUShort(writer, 0); // Num Match Groups.
        }
    }

    private void WriteVector(BinaryWriter writer, Vector3 value)
    {
        WriteFloat(writer, value.X);
        WriteFloat(writer, value.Y);
        WriteFloat(writer, value.Z);
    }

    private void WriteFloat(BinaryWriter writer, float value)
    {
        WriteInt(writer, BitConverter.SingleToInt32Bits(value));
    }

    private void WriteInt(BinaryWriter writer, int value)
    {
        writer.Write(Info.IsBigEndian ? BinaryPrimitives.ReverseEndianness(value) : value);
    }

    private void WriteUShort(BinaryWriter writer, ushort value)
    {
        writer.Write(Info.IsBigEndian ? BinaryPrimitives.ReverseEndianness(value) : value);
    }
}
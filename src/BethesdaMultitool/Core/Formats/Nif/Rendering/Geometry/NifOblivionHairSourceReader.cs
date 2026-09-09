using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>Proves the installed legacy static TES4 Hair cohort before color quantization.</summary>
internal static class NifOblivionHairSourceReader
{
    // Installed Style02: 10.2.0.0/user10/BS9, one unskinned shape, Prn attachment and two
    // static exporter directional lights owned by both root children and effects. These effects
    // are explicitly framed, not silently treated as absent. This admits only the proven layer
    // operation; it makes no claim that the viewer reproduces the exporter's light placement.
    internal static bool IsEligible(byte[] data, NifInfo nif, int shapeIndex)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        if (nif.IsBigEndian || !nif.HasInlineStrings || nif.BinaryVersion != 0x0A020000 ||
            nif.HeaderString != "Gamebryo File Format, Version 10.2.0.0" ||
            nif.UserVersion != 10 || nif.BsVersion != 9 || nif.Blocks.Count != 11)
        {
            return false;
        }

        try
        {
            var root = -1;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < nif.Blocks.Count; index++)
            {
                var type = nif.Blocks[index].TypeName;
                if (type is not ("NiNode" or "NiStringExtraData" or "NiTriShape" or
                    "NiTexturingProperty" or "NiSourceTexture" or "NiAlphaProperty" or
                    "NiVertexColorProperty" or "NiMaterialProperty" or "NiTriShapeData" or
                    "NiDirectionalLight"))
                {
                    return false;
                }

                counts.TryGetValue(type, out var count);
                counts[type] = count + 1;
                if (type == "NiNode")
                {
                    root = index;
                }
            }

            return counts.Count == 10 && counts.All(pair =>
                       pair.Value == (pair.Key == "NiDirectionalLight" ? 2 : 1)) &&
                   ReadRoot(data, nif, root, shapeIndex) && ReadShape(data, nif, shapeIndex);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    private static bool ReadRoot(byte[] data, NifInfo nif, int index, int shapeIndex)
    {
        using var root = OpenBlock(data, nif, index, "NiNode");
        if (root is null || string.IsNullOrWhiteSpace(ReadString(root)) || root.ReadUInt32() != 1 ||
            !ReadAttachment(data, nif, root.ReadInt32()) || root.ReadInt32() != -1 ||
            !ReadStaticAvTail(root) || root.ReadUInt32() != 3)
        {
            return false;
        }

        var children = new HashSet<int> { root.ReadInt32(), root.ReadInt32(), root.ReadInt32() };
        if (children.Count != 3 || !children.Remove(shapeIndex) || root.ReadUInt32() != 2)
        {
            return false;
        }

        var firstLight = root.ReadInt32();
        var secondLight = root.ReadInt32();
        return firstLight != secondLight && children.SetEquals([firstLight, secondLight]) &&
               Remaining(root) == 0 && ReadLight(data, nif, firstLight) && ReadLight(data, nif, secondLight);
    }

    private static bool ReadAttachment(byte[] data, NifInfo nif, int index)
    {
        using var extra = OpenBlock(data, nif, index, "NiStringExtraData");
        return extra is not null && ReadString(extra) == "Prn" &&
               ReadString(extra) == "Bip01 Head" && Remaining(extra) == 0;
    }

    private static bool ReadLight(byte[] data, NifInfo nif, int index)
    {
        using var light = OpenBlock(data, nif, index, "NiDirectionalLight");
        return light is not null && ReadString(light) == "__MAX_Default_Light" &&
               ReadStaticObjectTail(light) && ReadStaticAvTail(light) &&
               light.ReadByte() == 1 && light.ReadUInt32() == 0 && light.ReadSingle().Equals(1f) &&
               ReadConstantFloats(light, 3, 0f) && ReadConstantFloats(light, 6, 1f) && Remaining(light) == 0;
    }

    private static bool ReadShape(byte[] data, NifInfo nif, int index)
    {
        using var shape = OpenBlock(data, nif, index, "NiTriShape");
        if (shape is null || string.IsNullOrWhiteSpace(ReadString(shape)) || !ReadStaticObjectTail(shape) ||
            shape.ReadUInt16() != 0x10 || !ReadFiniteFloats(shape, 13) || shape.ReadUInt32() != 4)
        {
            return false;
        }

        var properties = new HashSet<int>();
        for (var property = 0; property < 4; property++)
        {
            var reference = shape.ReadInt32();
            if (!properties.Add(reference) || !ReadProperty(data, nif, reference))
            {
                return false;
            }
        }

        if (shape.ReadInt32() != -1 || !ReadGeometry(data, nif, shape.ReadInt32()))
        {
            return false;
        }

        return shape.ReadInt32() == -1 && shape.ReadByte() == 0 && Remaining(shape) == 0;
    }

    private static bool ReadProperty(byte[] data, NifInfo nif, int index)
    {
        if (index < 0 || index >= nif.Blocks.Count)
        {
            return false;
        }

        return nif.Blocks[index].TypeName switch
        {
            "NiMaterialProperty" => ReadMaterial(data, nif, index),
            "NiTexturingProperty" => ReadTexturing(data, nif, index),
            "NiAlphaProperty" => ReadAlpha(data, nif, index),
            "NiVertexColorProperty" => ReadVertexColor(data, nif, index),
            _ => false
        };
    }

    private static bool ReadMaterial(byte[] data, NifInfo nif, int index)
    {
        using var material = OpenBlock(data, nif, index, "NiMaterialProperty");
        return material is not null &&
               string.Equals(ReadString(material), "Hair", StringComparison.OrdinalIgnoreCase) &&
               ReadStaticObjectTail(material) && ReadConstantFloats(material, 6, 1f) &&
               ReadFiniteFloats(material, 3) && ReadConstantFloats(material, 3, 0f) &&
               float.IsFinite(material.ReadSingle()) && material.ReadSingle().Equals(1f) && Remaining(material) == 0;
    }

    private static bool ReadAlpha(byte[] data, NifInfo nif, int index)
    {
        using var alpha = OpenBlock(data, nif, index, "NiAlphaProperty");
        return alpha is not null && ReadString(alpha).Length == 0 && ReadStaticObjectTail(alpha) &&
               alpha.ReadUInt16() == 0x12ED && alpha.ReadByte() == 0 && Remaining(alpha) == 0;
    }

    private static bool ReadVertexColor(byte[] data, NifInfo nif, int index)
    {
        using var color = OpenBlock(data, nif, index, "NiVertexColorProperty");
        return color is not null && ReadString(color).Length == 0 && ReadStaticObjectTail(color) &&
               color.ReadUInt16() == 0 && color.ReadUInt32() == 2 && color.ReadUInt32() == 1 && Remaining(color) == 0;
    }

    private static bool ReadTexturing(byte[] data, NifInfo nif, int index)
    {
        using var texture = OpenBlock(data, nif, index, "NiTexturingProperty");
        if (texture is null || ReadString(texture).Length != 0 || !ReadStaticObjectTail(texture) ||
            texture.ReadUInt32() != 2 || texture.ReadUInt32() != 7 || texture.ReadByte() != 1)
        {
            return false;
        }

        var source = texture.ReadInt32();
        if (texture.ReadUInt32() != 3 || texture.ReadUInt32() != 2 || texture.ReadUInt32() != 0 ||
            texture.ReadInt16() != 0 || texture.ReadInt16() != -100 || texture.ReadByte() != 0)
        {
            return false;
        }

        // The PS2 L/K shorts above are present even in this PC legacy descriptor.
        for (var slot = 0; slot < 6; slot++)
        {
            if (texture.ReadByte() != 0)
            {
                return false;
            }
        }

        return texture.ReadUInt32() == 0 && Remaining(texture) == 0 && ReadSource(data, nif, source);
    }

    private static bool ReadSource(byte[] data, NifInfo nif, int index)
    {
        using var source = OpenBlock(data, nif, index, "NiSourceTexture");
        if (source is null || ReadString(source).Length != 0 || !ReadStaticObjectTail(source) || source.ReadByte() != 1)
        {
            return false;
        }

        var path = ReadString(source);
        return !string.IsNullOrWhiteSpace(path) && path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
               source.ReadInt32() == -1 && source.ReadUInt32() == 6 && source.ReadUInt32() == 1 &&
               source.ReadUInt32() == 3 && source.ReadByte() == 1 && source.ReadByte() == 1 && Remaining(source) == 0;
    }

    private static bool ReadGeometry(byte[] data, NifInfo nif, int index)
    {
        using var geometry = OpenBlock(data, nif, index, "NiTriShapeData");
        if (geometry is null || geometry.ReadUInt32() != 0)
        {
            return false;
        }

        var count = geometry.ReadUInt16();
        if (count < 3 || geometry.ReadUInt16() != 0 || geometry.ReadByte() != 1 ||
            !ReadFiniteFloats(geometry, count * 3) || geometry.ReadUInt16() != 1 ||
            geometry.ReadByte() != 1 || !ReadFiniteFloats(geometry, count * 3) ||
            !ReadFiniteFloats(geometry, 4) || geometry.ReadByte() != 1 || count * 16 > Remaining(geometry))
        {
            return false;
        }

        for (var vertex = 0; vertex < count; vertex++)
        {
            // Do not use quantized RGBA8: e.g. raw1.001 clamps to255 but does not meet this proof.
            if (!float.IsFinite(geometry.ReadSingle()) || !geometry.ReadSingle().Equals(1f) ||
                !float.IsFinite(geometry.ReadSingle()) || !geometry.ReadSingle().Equals(1f))
            {
                return false;
            }
        }

        // This version has no additional-data reference between consistency and topology.
        if (!ReadFiniteFloats(geometry, count * 2) || geometry.ReadUInt16() != 0)
        {
            return false;
        }

        return ReadTopology(geometry, count);
    }

    private static bool ReadTopology(BinaryReader geometry, int count)
    {
        var triangles = geometry.ReadUInt16();
        if (triangles == 0 || geometry.ReadUInt32() != triangles * 3u || geometry.ReadByte() != 1 ||
            triangles * 6 > Remaining(geometry))
        {
            return false;
        }

        for (var triangleIndex = 0; triangleIndex < triangles * 3; triangleIndex++)
        {
            if (geometry.ReadUInt16() >= count)
            {
                return false;
            }
        }

        return geometry.ReadUInt16() == 0 && Remaining(geometry) == 0;
    }

    private static bool ReadStaticAvTail(BinaryReader reader)
    {
        return reader.ReadUInt16() == 0x10 && ReadFiniteFloats(reader, 13) &&
               reader.ReadUInt32() == 0 && reader.ReadInt32() == -1;
    }

    private static bool ReadStaticObjectTail(BinaryReader reader)
    {
        return reader.ReadUInt32() == 0 && reader.ReadInt32() == -1;
    }

    private static bool ReadFiniteFloats(BinaryReader reader, int count)
    {
        if (count > Remaining(reader) / 4)
        {
            return false;
        }

        for (var component = 0; component < count; component++)
        {
            if (!float.IsFinite(reader.ReadSingle()))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadConstantFloats(BinaryReader reader, int count, float expected)
    {
        for (var component = 0; component < count; component++)
        {
            if (!reader.ReadSingle().Equals(expected))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadString(BinaryReader reader)
    {
        var count = reader.ReadUInt32();
        if (count > Remaining(reader))
        {
            throw new InvalidDataException("The inline name exceeds its owning NIF block.");
        }

        var bytes = reader.ReadBytes(checked((int)count));
        if (bytes.Contains((byte)0))
        {
            throw new InvalidDataException("An inline name contains a null byte.");
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static long Remaining(BinaryReader reader)
    {
        return reader.BaseStream.Length - reader.BaseStream.Position;
    }

    private static BinaryReader? OpenBlock(byte[] data, NifInfo nif, int index, string type)
    {
        if (index < 0 || index >= nif.Blocks.Count || nif.Blocks[index].TypeName != type)
        {
            return null;
        }

        var block = nif.Blocks[index];
        if (block.DataOffset < 0 || block.Size < 0 || block.Size > data.Length ||
            block.DataOffset > data.Length - block.Size)
        {
            return null;
        }

        return new BinaryReader(new MemoryStream(data, block.DataOffset, block.Size, false), Encoding.ASCII);
    }
}

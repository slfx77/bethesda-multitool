using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Proves a static PC TES4 ordinary-material source with no inherited environment override
///     or secondary normal. The returned authored base path is provenance; the bound normal's
///     effective format and current composed material still determine specular eligibility.
/// </summary>
internal sealed class NifOblivionOrdinarySourceReader
{
    private readonly byte[] _data;
    private readonly NifInfo _nif;

    private NifOblivionOrdinarySourceReader(byte[] data, NifInfo nif)
    {
        _data = data;
        _nif = nif;
    }

    // Installed retail oracle: specular-retail-routing-20260905 graphs21-26,38-43,58-64
    // under TestOutput/current-goal/06-oblivion-facgen/attachments-and-tails-20260905.
    // Fresh property +1C starts at zero. Serialized MODULATE2 becomes runtime masked bits4,
    // not the bits6 override. These are runtime facts, not a fabricated serialized shader flag.
    internal static string? ReadDiffusePath(byte[] data, NifInfo nif, int shapeIndex)
    {
        return Create(data, nif)?.ReadDiffusePath(shapeIndex);
    }

    /// <summary>
    ///     Validates scene inheritance once per extraction. The context is local to that read-only
    ///     invocation; no mutable global cache retains input arrays or NIF metadata.
    /// </summary>
    internal static NifOblivionOrdinarySourceReader? Create(byte[] data, NifInfo nif)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        if (nif.IsBigEndian || !nif.HasInlineStrings || nif.BinaryVersion != 0x14000004 ||
            nif.HeaderString != "Gamebryo File Format, Version 20.0.0.4" ||
            nif.UserVersion != 11 || nif.BsVersion != 11)
        {
            return null;
        }

        try
        {
            return ReadScene(data, nif) ? new NifOblivionOrdinarySourceReader(data, nif) : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    internal string? ReadDiffusePath(int shapeIndex)
    {
        try
        {
            return ReadShape(_data, _nif, shapeIndex);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private static bool ReadScene(byte[] data, NifInfo nif)
    {
        // Scan all nodes, including disconnected/skeleton nodes, rather than assuming the
        // extractor's effective property list represents TES4 inheritance. Unknown scene types,
        // effects, controllers, extra properties and already prepared shader families fail closed.
        for (var index = 0; index < nif.Blocks.Count; index++)
        {
            var type = nif.Blocks[index].TypeName;
            if (type == "NiNode")
            {
                if (!ReadNode(data, nif, index))
                {
                    return false;
                }
            }
            else if (type is not ("NiTriShape" or "NiTriShapeData" or "NiBinaryExtraData" or
                     "NiMaterialProperty" or "NiTexturingProperty" or "NiSourceTexture" or
                     "NiSkinInstance" or "NiSkinData" or "NiSkinPartition"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadNode(byte[] data, NifInfo nif, int index)
    {
        using var node = OpenBlock(data, nif, index, "NiNode");
        if (node is null)
        {
            return false;
        }

        _ = ReadString(node);
        if (!ReadStaticObjectTail(node) || node.ReadUInt16() is not (2 or 0x11) ||
            !ReadFiniteFloats(node, 13) || node.ReadUInt32() != 0 || node.ReadInt32() != -1)
        {
            return false;
        }

        var children = node.ReadUInt32();
        if (children > Remaining(node) / 4)
        {
            return false;
        }

        for (var child = 0u; child < children; child++)
        {
            var reference = node.ReadInt32();
            if (reference != -1 && !IsBlockType(nif, reference, "NiNode") &&
                !IsBlockType(nif, reference, "NiTriShape"))
            {
                return false;
            }
        }

        return node.ReadUInt32() == 0 && Remaining(node) == 0; // No inherited effects.
    }

    private static string? ReadShape(byte[] data, NifInfo nif, int shapeIndex)
    {
        using var shape = OpenBlock(data, nif, shapeIndex, "NiTriShape");
        if (shape is null || string.IsNullOrWhiteSpace(ReadString(shape)) || shape.ReadUInt32() != 1)
        {
            return null;
        }

        if (!IsBlockType(nif, shape.ReadInt32(), "NiBinaryExtraData") ||
            shape.ReadInt32() != -1 || shape.ReadUInt16() != 0x0016 ||
            !ReadFiniteFloats(shape, 13) || shape.ReadUInt32() != 2)
        {
            return null;
        }

        var first = shape.ReadInt32();
        var second = shape.ReadInt32();
        var material = IsBlockType(nif, first, "NiMaterialProperty") ? first : second;
        var texturing = material == first ? second : first;
        if (!ReadMaterial(data, nif, material) || shape.ReadInt32() != -1)
        {
            return null;
        }

        var geometry = shape.ReadInt32();
        var skin = shape.ReadInt32();
        if ((skin != -1 && !IsBlockType(nif, skin, "NiSkinInstance")) ||
            shape.ReadByte() != 0 || Remaining(shape) != 0 ||
            !ReadGeometry(data, nif, shapeIndex, geometry))
        {
            return null;
        }

        return ReadTexturing(data, nif, texturing);
    }

    private static bool ReadMaterial(byte[] data, NifInfo nif, int index)
    {
        using var material = OpenBlock(data, nif, index, "NiMaterialProperty");
        if (material is null || !OblivionOrdinarySpecularPolicy.IsOrdinaryMaterialName(ReadString(material)) ||
            !ReadStaticObjectTail(material) || Remaining(material) != 56)
        {
            return false;
        }

        // The audited ordinary cohort is opaque, unemissive, with white ambient/diffuse.
        // These restrictions bound the supported variant; white specular never enables a pass.
        for (var component = 0; component < 6; component++)
        {
            if (!material.ReadSingle().Equals(1f))
            {
                return false;
            }
        }

        if (!ReadFiniteFloats(material, 3))
        {
            return false;
        }

        for (var component = 0; component < 3; component++)
        {
            if (!material.ReadSingle().Equals(0f))
            {
                return false;
            }
        }

        return float.IsFinite(material.ReadSingle()) && material.ReadSingle().Equals(1f);
    }

    private static string? ReadTexturing(byte[] data, NifInfo nif, int index)
    {
        using var texture = OpenBlock(data, nif, index, "NiTexturingProperty");
        if (texture is null)
        {
            return null;
        }

        _ = ReadString(texture);
        if (!ReadStaticObjectTail(texture) || texture.ReadUInt32() != 2 ||
            texture.ReadUInt32() != 7 || texture.ReadByte() != 1)
        {
            return null;
        }

        var source = texture.ReadInt32();
        if (texture.ReadUInt32() != 3 || texture.ReadUInt32() != 2 ||
            texture.ReadUInt32() != 0 || texture.ReadByte() != 0)
        {
            return null;
        }

        for (var slot = 0; slot < 6; slot++) // Dark/detail/gloss/glow/bump/decal0, no normal slot.
        {
            if (texture.ReadByte() != 0)
            {
                return null;
            }
        }

        return texture.ReadUInt32() == 0 && Remaining(texture) == 0
            ? ReadSourceTexture(data, nif, source) : null;
    }

    private static string? ReadSourceTexture(byte[] data, NifInfo nif, int index)
    {
        using var source = OpenBlock(data, nif, index, "NiSourceTexture");
        if (source is null)
        {
            return null;
        }

        _ = ReadString(source);
        if (!ReadStaticObjectTail(source) || source.ReadByte() != 1)
        {
            return null;
        }

        var path = ReadString(source);
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
            Remaining(source) != 18 || source.ReadInt32() != -1 || source.ReadUInt32() != 6 ||
            source.ReadUInt32() != 1 || source.ReadUInt32() != 3 ||
            source.ReadByte() != 1 || source.ReadByte() != 1)
        {
            return null;
        }

        return path;
    }

    private static bool ReadGeometry(byte[] data, NifInfo nif, int shapeIndex, int geometryIndex)
    {
        using var geometry = OpenBlock(data, nif, geometryIndex, "NiTriShapeData");
        if (geometry is null)
        {
            return false;
        }

        _ = geometry.ReadUInt32();
        var count = geometry.ReadUInt16();
        if (count == 0 || geometry.ReadUInt16() != 0 || geometry.ReadByte() != 1 ||
            !ReadFiniteFloats(geometry, count * 3) || geometry.ReadUInt16() != 1 ||
            geometry.ReadByte() != 1 || count * 12 > Remaining(geometry))
        {
            return false;
        }

        var normals = new float[count * 3];
        for (var component = 0; component < normals.Length; component++)
        {
            normals[component] = geometry.ReadSingle();
        }

        if (!ReadFiniteFloats(geometry, 4) || geometry.ReadByte() != 0 ||
            !ReadFiniteFloats(geometry, count * 2))
        {
            return false;
        }

        var consistency = geometry.ReadUInt16();
        if (consistency is not (0 or 0x4000 or 0x8000) || geometry.ReadInt32() != -1 ||
            !ReadTopology(geometry, count))
        {
            return false;
        }

        return NifOblivionTangentExtraReader.Read(data, nif, shapeIndex, count, normals) is not null;
    }

    private static bool ReadTopology(BinaryReader reader, int vertices)
    {
        var triangles = reader.ReadUInt16();
        if (triangles == 0 || reader.ReadUInt32() != triangles * 3u || reader.ReadByte() != 1 ||
            triangles * 6 > Remaining(reader))
        {
            return false;
        }

        for (var index = 0; index < triangles * 3; index++)
        {
            if (reader.ReadUInt16() >= vertices)
            {
                return false;
            }
        }

        var groups = reader.ReadUInt16();
        for (var group = 0; group < groups; group++)
        {
            var count = reader.ReadUInt16();
            if (count > Remaining(reader) / 2)
            {
                return false;
            }

            reader.BaseStream.Position += count * 2; // Legacy match indices do not replace authored N/T/B.
        }

        return Remaining(reader) == 0;
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

    private static bool ReadStaticObjectTail(BinaryReader reader)
    {
        return reader.ReadUInt32() == 0 && reader.ReadInt32() == -1;
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

    private static long Remaining(BinaryReader reader) => reader.BaseStream.Length - reader.BaseStream.Position;

    private static bool IsBlockType(NifInfo nif, int index, string type) =>
        index >= 0 && index < nif.Blocks.Count && nif.Blocks[index].TypeName == type;

    private static BinaryReader? OpenBlock(byte[] data, NifInfo nif, int index, string type)
    {
        if (!IsBlockType(nif, index, type))
        {
            return null;
        }

        var block = nif.Blocks[index];
        if (block.DataOffset < 0 || block.Size < 0 || block.Size > data.Length ||
            block.DataOffset > data.Length - block.Size)
        {
            return null;
        }

        return new BinaryReader(new MemoryStream(data, block.DataOffset, block.Size, writable: false),
            Encoding.ASCII);
    }
}

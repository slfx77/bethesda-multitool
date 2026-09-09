using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Proves the bounded PC Oblivion source subset used by the body SKIN2000 policy.
///     Unlike null shader metadata, this inspects the owning legacy properties and authored
///     streams. Unknown variants fail closed; it neither selects a shader nor changes geometry.
/// </summary>
internal sealed class NifOblivionBodySkinSourceReader
{
    private readonly byte[] _data;
    private readonly bool _hasStrictStaticScene;
    private readonly NifInfo _nif;

    private NifOblivionBodySkinSourceReader(byte[] data, NifInfo nif, bool hasStrictStaticScene)
    {
        _data = data;
        _nif = nif;
        _hasStrictStaticScene = hasStrictStaticScene;
    }

    internal static bool IsEligible(byte[] data, NifInfo nif, int shapeIndex)
    {
        return Create(data, nif)?.ReadAmbientColor(shapeIndex) is not null;
    }

    /// <summary>Local read-only extraction context; scene inheritance is validated once.</summary>
    // Format/ownership oracle: TestOutput/current-goal/06-oblivion-facgen/
    // attachments-and-tails-20260905/body-skin-routing-20260905/authored-audit.
    internal static NifOblivionBodySkinSourceReader? Create(byte[] data, NifInfo nif)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        if (nif.IsBigEndian || !nif.HasInlineStrings || nif.BinaryVersion != 0x14000004 ||
            nif.HeaderString != "Gamebryo File Format, Version 20.0.0.4" ||
            nif.UserVersion != 11 || nif.BsVersion != 11 ||
            nif.Blocks.Any(block => block.TypeName == "NiTextureEffect"))
        {
            return null;
        }

        // A malformed/unsupported inherited scene excludes only the newly admitted nonwhite
        // cohort. Preserve the established white-material admission and all its shape checks.
        return new NifOblivionBodySkinSourceReader(data, nif, TryReadStrictStaticScene(data, nif));
    }

    private static bool TryReadStrictStaticScene(byte[] data, NifInfo nif)
    {
        try
        {
            return ReadStrictStaticScene(data, nif);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException)
        {
            // The owning shape still gets its independent bounded validation below.
            return false;
        }
    }

    internal (float R, float G, float B)? ReadAmbientColor(int shapeIndex)
    {
        try
        {
            return ReadSource(_data, _nif, shapeIndex, _hasStrictStaticScene);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private static (float R, float G, float B)? ReadSource(byte[] data, NifInfo nif, int shapeIndex,
        bool hasStrictStaticScene)
    {
        using var shape = OpenBlock(data, nif, shapeIndex, "NiTriShape");
        if (shape is null || string.IsNullOrWhiteSpace(ReadString(shape)) ||
            shape.ReadUInt32() != 1)
        {
            return null;
        }

        var tangentExtra = shape.ReadInt32();
        if (!IsBlockType(nif, tangentExtra, "NiBinaryExtraData") ||
            shape.ReadInt32() != -1 || shape.ReadUInt16() != 0x0016 ||
            !ReadFiniteFloats(shape, 13) || shape.ReadUInt32() != 2)
        {
            return null;
        }

        var firstProperty = shape.ReadInt32();
        var secondProperty = shape.ReadInt32();
        var materialIndex = IsBlockType(nif, firstProperty, "NiMaterialProperty")
            ? firstProperty
            : secondProperty;
        var textureIndex = materialIndex == firstProperty ? secondProperty : firstProperty;
        if (!ReadMaterial(data, nif, materialIndex, hasStrictStaticScene, out var ambient) ||
            !ReadTexturing(data, nif, textureIndex) ||
            shape.ReadInt32() != -1) // Collision object
        {
            return null;
        }

        var geometryIndex = shape.ReadInt32();
        var skinIndex = shape.ReadInt32();
        if ((skinIndex != -1 && !IsBlockType(nif, skinIndex, "NiSkinInstance")) ||
            shape.ReadByte() != 0 || Remaining(shape) != 0) // Has Shader / no extra variant
        {
            return null;
        }

        return ReadGeometry(data, nif, shapeIndex, geometryIndex) ? ambient : null;
    }

    private static bool ReadMaterial(byte[] data, NifInfo nif, int index, bool hasStrictStaticScene,
        out (float R, float G, float B) ambient)
    {
        ambient = default;
        using var material = OpenBlock(data, nif, index, "NiMaterialProperty");
        if (material is null ||
            !string.Equals(ReadString(material), "skin", StringComparison.OrdinalIgnoreCase) ||
            !ReadStaticObjectTail(material) || Remaining(material) != 56)
        {
            return false;
        }

        // Retail graphs65-68 join the absent-color load/clone path to SKIN2000 Toggles.x=0.
        // Property+94 has an independent runtime DarknessEffect producer.
        // Keep the raw ambient as provenance; no invented material multiplier is applied.
        ambient = (material.ReadSingle(), material.ReadSingle(), material.ReadSingle());
        if (!float.IsFinite(ambient.R) || !float.IsFinite(ambient.G) || !float.IsFinite(ambient.B) ||
            (ambient != (1f, 1f, 1f) && !hasStrictStaticScene))
        {
            return false;
        }

        // White diffuse, finite specular/gloss, black emission and opaque alpha remain required.
        for (var component = 0; component < 3; component++)
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

    private static bool ReadStrictStaticScene(byte[] data, NifInfo nif)
    {
        // This additional cohort is the proven static PC tail scene: root flags2, bone flags10,
        // no inherited overrides, ordinary geometry/skin blocks. Scan disconnected nodes too.
        var hasNode = false;
        for (var index = 0; index < nif.Blocks.Count; index++)
        {
            var type = nif.Blocks[index].TypeName;
            if (type == "NiNode")
            {
                hasNode = true;
                if (!ReadStaticNode(data, nif, index))
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

        return hasNode;
    }

    private static bool ReadStaticNode(byte[] data, NifInfo nif, int index)
    {
        using var node = OpenBlock(data, nif, index, "NiNode");
        if (node is null)
        {
            return false;
        }

        _ = ReadString(node);
        if (!ReadStaticObjectTail(node) || node.ReadUInt16() is not (2 or 0x10) ||
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

        return node.ReadUInt32() == 0 && Remaining(node) == 0;
    }

    private static bool ReadTexturing(byte[] data, NifInfo nif, int index)
    {
        using var texture = OpenBlock(data, nif, index, "NiTexturingProperty");
        if (texture is null)
        {
            return false;
        }

        _ = ReadString(texture);
        if (!ReadStaticObjectTail(texture) ||
            texture.ReadUInt32() != 2 || texture.ReadUInt32() != 7 ||
            texture.ReadByte() != 1)
        {
            return false;
        }

        var source = texture.ReadInt32();
        if (texture.ReadUInt32() != 3 || texture.ReadUInt32() != 2 ||
            texture.ReadUInt32() != 0 || texture.ReadByte() != 0)
        {
            return false; // Different clamp/filter/UV set or transformed coordinates.
        }

        // At 20.0.0.4 the six remaining flags are dark/detail/gloss/glow/bump/decal0.
        // There is no normal slot at this version. Extra descriptors are not skipped.
        for (var slot = 0; slot < 6; slot++)
        {
            if (texture.ReadByte() != 0)
            {
                return false;
            }
        }

        return texture.ReadUInt32() == 0 && Remaining(texture) == 0 &&
               ReadSourceTexture(data, nif, source);
    }

    private static bool ReadSourceTexture(byte[] data, NifInfo nif, int index)
    {
        using var source = OpenBlock(data, nif, index, "NiSourceTexture");
        if (source is null)
        {
            return false;
        }

        _ = ReadString(source);
        if (!ReadStaticObjectTail(source) || source.ReadByte() != 1 ||
            string.IsNullOrWhiteSpace(ReadString(source)) || Remaining(source) != 18)
        {
            return false;
        }

        // External file, default format preferences, static/direct rendering. Preferences
        // are not DDS pixel-format evidence and never select the shader's specular behavior.
        return source.ReadInt32() == -1 && source.ReadUInt32() == 6 &&
               source.ReadUInt32() == 1 && source.ReadUInt32() == 3 &&
               source.ReadByte() == 1 && source.ReadByte() == 1;
    }

    private static bool ReadGeometry(byte[] data, NifInfo nif, int shapeIndex, int geometryIndex)
    {
        using var geometry = OpenBlock(data, nif, geometryIndex, "NiTriShapeData");
        if (geometry is null)
        {
            return false;
        }

        _ = geometry.ReadUInt32(); // Group ID
        var count = geometry.ReadUInt16();
        if (count == 0 || geometry.ReadUInt16() != 0 || geometry.ReadByte() != 1 ||
            !ReadFiniteFloats(geometry, count * 3) || geometry.ReadUInt16() != 1 ||
            geometry.ReadByte() != 1)
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
            return false; // No vertex colors; exactly one finite authored UV set.
        }

        var consistency = geometry.ReadUInt16();
        if (consistency is not (0 or 0x4000 or 0x8000) || geometry.ReadInt32() != -1 ||
            !ReadTopology(geometry, count))
        {
            return false;
        }

        // The existing reader verifies the named owning attachment, both exact payload lengths,
        // and finite nonzero N/T/B, preserving mirrored/nonorthogonal authored frames.
        return NifOblivionTangentExtraReader.Read(data, nif, shapeIndex, count, normals) is not null;
    }

    private static bool ReadTopology(BinaryReader geometry, int vertices)
    {
        var triangles = geometry.ReadUInt16();
        if (triangles == 0 || geometry.ReadUInt32() != triangles * 3u || geometry.ReadByte() != 1 ||
            !ReadIndices(geometry, triangles * 3, vertices))
        {
            return false;
        }

        var groups = geometry.ReadUInt16();
        for (var group = 0; group < groups; group++)
        {
            var count = geometry.ReadUInt16();
            if (count > Remaining(geometry) / 2)
            {
                return false;
            }

            // These legacy shared-normal groups are not shader inputs. Stock cuirass Arms
            // retains 41 group indices beyond its current 444 vertices, while every drawable
            // triangle and authored N/T/B stream is valid. Check framing, not stale indices;
            // do not rebuild normals from the groups or reject their unrelated metadata.
            geometry.BaseStream.Position += count * 2;
        }

        return Remaining(geometry) == 0;
    }

    private static bool ReadIndices(BinaryReader reader, int count, int vertices)
    {
        if (count > Remaining(reader) / 2)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            if (reader.ReadUInt16() >= vertices)
            {
                return false;
            }
        }

        return true;
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

    private static long Remaining(BinaryReader reader)
    {
        return reader.BaseStream.Length - reader.BaseStream.Position;
    }

    private static bool IsBlockType(NifInfo nif, int index, string type)
    {
        return index >= 0 && index < nif.Blocks.Count && nif.Blocks[index].TypeName == type;
    }

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

        return new BinaryReader(new MemoryStream(data, block.DataOffset, block.Size, false),
            Encoding.ASCII);
    }
}

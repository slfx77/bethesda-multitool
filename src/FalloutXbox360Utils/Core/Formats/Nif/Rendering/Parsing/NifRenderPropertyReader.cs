using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;

/// <summary>
///     Reads render-state properties attached to geometry blocks.
/// </summary>
internal static class NifRenderPropertyReader
{
    private const uint LegacyMaterialPropertyFlagsMaxVersion = 0x0A000102;
    private const float DefaultMaterialGlossiness = 10f;

    /// <summary>
    ///     Check if any NiStencilProperty in the property refs has DrawMode = DRAW_BOTH (3).
    /// </summary>
    internal static bool ReadIsDoubleSided(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
            {
                continue;
            }

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName != "NiStencilProperty")
            {
                continue;
            }

            var pos = propBlock.DataOffset;
            var end = propBlock.DataOffset + propBlock.Size;
            if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian))
            {
                return false;
            }

            if (pos + 2 > end)
            {
                return false;
            }

            var flags = BinaryUtils.ReadUInt16(data, pos, nif.IsBigEndian);
            var drawMode = (flags >> 10) & 0x3;
            return drawMode == 3;
        }

        return false;
    }

    /// <summary>
    ///     Read NiAlphaProperty to extract blend/test flags, threshold, and blend modes.
    /// </summary>
    internal static AlphaPropertyInfo ReadAlphaProperty(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        const byte defaultThreshold = 128;
        const byte defaultTestFunction = 4;
        const byte defaultSrcBlend = 6;
        const byte defaultDstBlend = 7;

        var defaultInfo = new AlphaPropertyInfo(
            false,
            false,
            defaultThreshold,
            defaultTestFunction,
            defaultSrcBlend,
            defaultDstBlend);

        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
            {
                continue;
            }

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName != "NiAlphaProperty")
            {
                continue;
            }

            var pos = propBlock.DataOffset;
            var end = propBlock.DataOffset + propBlock.Size;
            if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian))
            {
                return defaultInfo;
            }

            if (pos + 2 > end)
            {
                return defaultInfo;
            }

            var alphaFlags = BinaryUtils.ReadUInt16(data, pos, nif.IsBigEndian);
            pos += 2;

            if (pos + 1 > end)
            {
                return defaultInfo;
            }

            var threshold = data[pos];
            return new AlphaPropertyInfo(
                (alphaFlags & 1) != 0,
                (alphaFlags & (1 << 9)) != 0,
                threshold,
                (byte)((alphaFlags >> 10) & 0x7),
                (byte)((alphaFlags >> 1) & 0xF),
                (byte)((alphaFlags >> 5) & 0xF));
        }

        return defaultInfo;
    }

    /// <summary>
    ///     Read material alpha from NiMaterialProperty.
    /// </summary>
    internal static float ReadMaterialAlpha(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return ReadMaterialProperty(data, nif, propertyRefs).Alpha;
    }

    /// <summary>
    ///     Read material specular color from NiMaterialProperty (3 floats: R, G, B).
    /// </summary>
    internal static (float R, float G, float B) ReadMaterialSpecularColor(byte[] data, NifInfo nif,
        List<int> propertyRefs)
    {
        var info = ReadMaterialProperty(data, nif, propertyRefs);
        return (info.SpecularR, info.SpecularG, info.SpecularB);
    }

    /// <summary>
    ///     Read material glossiness from NiMaterialProperty.
    /// </summary>
    internal static float ReadMaterialGlossiness(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return ReadMaterialProperty(data, nif, propertyRefs).Glossiness;
    }

    internal static MaterialPropertyInfo ReadMaterialProperty(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        var defaultInfo = new MaterialPropertyInfo(1f, DefaultMaterialGlossiness, 0f, 0f, 0f);

        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
            {
                continue;
            }

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName != "NiMaterialProperty")
            {
                continue;
            }

            var pos = propBlock.DataOffset;
            var end = pos + propBlock.Size;
            if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian))
            {
                return defaultInfo;
            }

            var specularColorOffset = pos + GetMaterialSpecularColorOffset(nif);
            var glossinessOffset = pos + GetMaterialGlossinessOffset(nif);
            var alphaOffset = pos + GetMaterialAlphaOffset(nif);
            if (glossinessOffset + 4 > end || alphaOffset + 4 > end)
            {
                return defaultInfo;
            }

            var specR = 0f;
            var specG = 0f;
            var specB = 0f;
            if (specularColorOffset + 12 <= end)
            {
                specR = BinaryUtils.ReadFloat(data, specularColorOffset, nif.IsBigEndian);
                specG = BinaryUtils.ReadFloat(data, specularColorOffset + 4, nif.IsBigEndian);
                specB = BinaryUtils.ReadFloat(data, specularColorOffset + 8, nif.IsBigEndian);
            }

            return new MaterialPropertyInfo(
                BinaryUtils.ReadFloat(data, alphaOffset, nif.IsBigEndian),
                BinaryUtils.ReadFloat(data, glossinessOffset, nif.IsBigEndian),
                specR, specG, specB);
        }

        return defaultInfo;
    }

    private static int GetMaterialGlossinessOffset(NifInfo nif)
    {
        return GetMaterialSpecularColorOffset(nif) + 12 + 12;
    }

    private static int GetMaterialAlphaOffset(NifInfo nif)
    {
        return GetMaterialGlossinessOffset(nif) + 4;
    }

    private static int GetMaterialSpecularColorOffset(NifInfo nif)
    {
        var offset = 0;

        // Legacy NIF versions stored a NiMaterialProperty flags field before the color data.
        if (nif.BinaryVersion != 0 &&
            nif.BinaryVersion <= LegacyMaterialPropertyFlagsMaxVersion)
        {
            offset += 2;
        }

        // Older BS versions include ambient + diffuse colors; Fallout 3 / New Vegas do not.
        if (nif.BsVersion < 26)
        {
            offset += 24;
        }

        return offset;
    }

    /// <summary>
    ///     Read animated emissive color from NiMaterialColorController → NiPoint3Interpolator → NiPosData.
    ///     Returns the first keyframe's RGB color, or null if no emissive controller exists.
    /// </summary>
    internal static (float R, float G, float B)? ReadAnimatedEmissiveColor(
        byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
                continue;

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName != "NiMaterialProperty")
                continue;

            // Read controller ref from NiObjectNET header
            var controllerRef = NifBinaryCursor.ReadNiObjectNETControllerRef(
                data, propBlock.DataOffset, propBlock.DataOffset + propBlock.Size, nif.IsBigEndian);
            if (controllerRef < 0 || controllerRef >= nif.Blocks.Count)
                continue;

            var ctrlBlock = nif.Blocks[controllerRef];
            if (ctrlBlock.TypeName != "NiMaterialColorController")
                continue;

            // NiMaterialColorController inherits NiTimeController (NOT NiObjectNET).
            // Layout from block data start:
            //   NiTimeController: nextCtrl(4) + flags(2) + freq(4) + phase(4) +
            //                     startTime(4) + stopTime(4) + target(4) = 26 bytes
            //   NiSingleInterpController: interpolator ref (4)
            //   NiMaterialColorController: targetColor (2)
            var ctrlPos = ctrlBlock.DataOffset;
            var ctrlEnd = ctrlPos + ctrlBlock.Size;

            // Skip NiTimeController fields (26 bytes) to reach interpolator ref
            ctrlPos += 26;
            if (ctrlPos + 6 > ctrlEnd)
                continue;

            var interpolatorRef = BinaryUtils.ReadInt32(data, ctrlPos, nif.IsBigEndian);
            var targetColor = BinaryUtils.ReadUInt16(data, ctrlPos + 4, nif.IsBigEndian);

            // targetColor: 3 = TC_SELF_ILLUM (emissive). Also accept 1 = TC_DIFFUSE
            // as it can serve a similar glow purpose.
            if (targetColor != 3 && targetColor != 1)
                continue;

            if (interpolatorRef < 0 || interpolatorRef >= nif.Blocks.Count)
                continue;

            var interpBlock = nif.Blocks[interpolatorRef];
            if (interpBlock.TypeName != "NiPoint3Interpolator")
                continue;

            // NiPoint3Interpolator: NiObjectNET header skipped,
            // then: value (Vector3 = 12 bytes) + data ref (4 bytes)
            var interpPos = interpBlock.DataOffset;
            var interpEnd = interpPos + interpBlock.Size;

            // NiPoint3Interpolator is a NiKeyBasedInterpolator (no NiObjectNET header).
            // Layout: value.x(4) + value.y(4) + value.z(4) + dataRef(4) = 16 bytes
            if (interpPos + 16 > interpEnd)
                continue;

            // Read the static value from the interpolator (fallback if no keyframes)
            var staticR = BinaryUtils.ReadFloat(data, interpPos, nif.IsBigEndian);
            var staticG = BinaryUtils.ReadFloat(data, interpPos + 4, nif.IsBigEndian);
            var staticB = BinaryUtils.ReadFloat(data, interpPos + 8, nif.IsBigEndian);
            var dataRef = BinaryUtils.ReadInt32(data, interpPos + 12, nif.IsBigEndian);

            // Try to read first keyframe from NiPosData for a more accurate value
            if (dataRef >= 0 && dataRef < nif.Blocks.Count)
            {
                var dataBlock = nif.Blocks[dataRef];
                if (dataBlock.TypeName == "NiPosData")
                {
                    var dataPos = dataBlock.DataOffset;
                    var dataEnd = dataPos + dataBlock.Size;
                    if (dataPos + 8 <= dataEnd)
                    {
                        var numKeys = BinaryUtils.ReadInt32(data, dataPos, nif.IsBigEndian);
                        if (numKeys > 0)
                        {
                            dataPos += 8; // skip numKeys + interpolation type
                            dataPos += 4; // skip first key's time value
                            if (dataPos + 12 <= dataEnd)
                            {
                                return (
                                    BinaryUtils.ReadFloat(data, dataPos, nif.IsBigEndian),
                                    BinaryUtils.ReadFloat(data, dataPos + 4, nif.IsBigEndian),
                                    BinaryUtils.ReadFloat(data, dataPos + 8, nif.IsBigEndian));
                            }
                        }
                    }
                }
            }

            // Fall back to interpolator's static value
            if (staticR > 0f || staticG > 0f || staticB > 0f)
                return (staticR, staticG, staticB);
        }

        return null;
    }

    internal readonly record struct AlphaPropertyInfo(
        bool HasAlphaBlend,
        bool HasAlphaTest,
        byte AlphaTestThreshold,
        byte AlphaTestFunction,
        byte SrcBlendMode,
        byte DstBlendMode);

    internal readonly record struct MaterialPropertyInfo(
        float Alpha,
        float Glossiness,
        float SpecularR,
        float SpecularG,
        float SpecularB);
}

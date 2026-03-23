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
    ///     Read material glossiness from NiMaterialProperty.
    /// </summary>
    internal static float ReadMaterialGlossiness(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return ReadMaterialProperty(data, nif, propertyRefs).Glossiness;
    }

    internal static MaterialPropertyInfo ReadMaterialProperty(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        var defaultInfo = new MaterialPropertyInfo(1f, DefaultMaterialGlossiness);

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

            var glossinessOffset = pos + GetMaterialGlossinessOffset(nif);
            var alphaOffset = pos + GetMaterialAlphaOffset(nif);
            if (glossinessOffset + 4 > end || alphaOffset + 4 > end)
            {
                return defaultInfo;
            }

            return new MaterialPropertyInfo(
                BinaryUtils.ReadFloat(data, alphaOffset, nif.IsBigEndian),
                BinaryUtils.ReadFloat(data, glossinessOffset, nif.IsBigEndian));
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

    internal readonly record struct AlphaPropertyInfo(
        bool HasAlphaBlend,
        bool HasAlphaTest,
        byte AlphaTestThreshold,
        byte AlphaTestFunction,
        byte SrcBlendMode,
        byte DstBlendMode);

    internal readonly record struct MaterialPropertyInfo(
        float Alpha,
        float Glossiness);
}

using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;

/// <summary>
///     Reads render-state properties attached to geometry blocks.
/// </summary>
internal static class NifRenderPropertyReader
{
    internal readonly record struct AlphaPropertyInfo(
        bool HasAlphaBlend,
        bool HasAlphaTest,
        byte AlphaTestThreshold,
        byte AlphaTestFunction,
        byte SrcBlendMode,
        byte DstBlendMode);

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
            var drawMode = (flags >> 11) & 0x3;
            return drawMode == 3;
        }

        return true;
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
            HasAlphaBlend: false,
            HasAlphaTest: false,
            AlphaTestThreshold: defaultThreshold,
            AlphaTestFunction: defaultTestFunction,
            SrcBlendMode: defaultSrcBlend,
            DstBlendMode: defaultDstBlend);

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
                HasAlphaBlend: (alphaFlags & 1) != 0,
                HasAlphaTest: (alphaFlags & (1 << 9)) != 0,
                AlphaTestThreshold: threshold,
                AlphaTestFunction: (byte)((alphaFlags >> 10) & 0x7),
                SrcBlendMode: (byte)((alphaFlags >> 1) & 0xF),
                DstBlendMode: (byte)((alphaFlags >> 5) & 0xF));
        }

        return defaultInfo;
    }

    /// <summary>
    ///     Read material alpha from NiMaterialProperty.
    /// </summary>
    internal static float ReadMaterialAlpha(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
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
                return 1f;
            }

            var alphaOffset = pos + 52;
            if (alphaOffset + 4 > end)
            {
                return 1f;
            }

            return BinaryUtils.ReadFloat(data, alphaOffset, nif.IsBigEndian);
        }

        return 1f;
    }
}

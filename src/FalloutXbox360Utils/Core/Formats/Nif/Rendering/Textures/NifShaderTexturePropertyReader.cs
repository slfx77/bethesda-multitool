using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Reads shader property blocks to resolve texture set paths and shader flags.
/// </summary>
internal static class NifShaderTexturePropertyReader
{
    internal static string? ResolveDiffusePath(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (!TryGetPropertyBlock(nif, propRef, out var propBlock))
            {
                continue;
            }

            if (propBlock.TypeName == "BSShaderPPLightingProperty")
            {
                return ResolvePPLightingTexture(data, nif, propBlock, slotIndex: 0);
            }

            if (propBlock.TypeName == "BSShaderNoLightingProperty")
            {
                return ResolveNoLightingTexture(data, nif, propBlock);
            }
        }

        return null;
    }

    internal static string? ResolveNormalMapPath(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (!TryGetPropertyBlock(nif, propRef, out var propBlock) ||
                propBlock.TypeName != "BSShaderPPLightingProperty")
            {
                continue;
            }

            return ResolvePPLightingTexture(data, nif, propBlock, slotIndex: 1);
        }

        return null;
    }

    internal static uint? ReadShaderFlags2(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (!TryGetPropertyBlock(nif, propRef, out var propBlock) ||
                !IsShaderProperty(propBlock))
            {
                continue;
            }

            if (!TryReadShaderPropertyStart(data, propBlock, nif.IsBigEndian, out var pos, out var end))
            {
                return null;
            }

            if (pos + 12 > end)
            {
                return null;
            }

            pos += 8;
            return BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        }

        return null;
    }

    internal static (uint ShaderFlags, uint ShaderFlags2)? ReadShaderFlagsBoth(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (!TryGetPropertyBlock(nif, propRef, out var propBlock) ||
                !IsShaderProperty(propBlock))
            {
                continue;
            }

            if (!TryReadShaderPropertyStart(data, propBlock, nif.IsBigEndian, out var pos, out var end))
            {
                return null;
            }

            if (pos + 12 > end)
            {
                return null;
            }

            pos += 4;
            var shaderFlags = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
            pos += 4;
            var shaderFlags2 = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
            return (shaderFlags, shaderFlags2);
        }

        return null;
    }

    internal static (uint ShaderFlags, float EnvMapScale)? ReadEnvMapInfo(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (!TryGetPropertyBlock(nif, propRef, out var propBlock) ||
                propBlock.TypeName != "BSShaderPPLightingProperty")
            {
                continue;
            }

            if (!TryReadShaderPropertyStart(data, propBlock, nif.IsBigEndian, out var pos, out var end))
            {
                return null;
            }

            if (pos + 16 > end)
            {
                return null;
            }

            pos += 4;
            var shaderFlags = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
            pos += 8;
            var envMapScale = BinaryUtils.ReadFloat(data, pos, nif.IsBigEndian);
            return (shaderFlags, envMapScale);
        }

        return null;
    }

    private static bool TryGetPropertyBlock(
        NifInfo nif,
        int propRef,
        out BlockInfo propBlock)
    {
        propBlock = null!;
        if (propRef < 0 || propRef >= nif.Blocks.Count)
        {
            return false;
        }

        propBlock = nif.Blocks[propRef];
        return true;
    }

    private static bool IsShaderProperty(BlockInfo propBlock)
        => propBlock.TypeName is
            "BSShaderPPLightingProperty" or
            "BSShaderNoLightingProperty";

    private static string? ResolvePPLightingTexture(
        byte[] data,
        NifInfo nif,
        BlockInfo propBlock,
        int slotIndex)
    {
        if (!TryReadTextureSetRef(data, nif, propBlock, out var textureSetRef) ||
            textureSetRef < 0 ||
            textureSetRef >= nif.Blocks.Count)
        {
            return null;
        }

        var textureSetBlock = nif.Blocks[textureSetRef];
        if (textureSetBlock.TypeName != "BSShaderTextureSet")
        {
            return null;
        }

        return ReadTextureSetSlot(
            data,
            textureSetBlock,
            nif.IsBigEndian,
            slotIndex);
    }

    private static bool TryReadTextureSetRef(
        byte[] data,
        NifInfo nif,
        BlockInfo propBlock,
        out int textureSetRef)
    {
        textureSetRef = -1;
        if (!TryReadShaderPropertyStart(data, propBlock, nif.IsBigEndian, out var pos, out var end))
        {
            return false;
        }

        if (pos + 16 + 4 + 4 > end)
        {
            return false;
        }

        pos += 16;
        pos += 4;
        textureSetRef = BinaryUtils.ReadInt32(data, pos, nif.IsBigEndian);
        return true;
    }

    private static string? ResolveNoLightingTexture(
        byte[] data,
        NifInfo nif,
        BlockInfo propBlock)
    {
        if (!TryReadShaderPropertyStart(data, propBlock, nif.IsBigEndian, out var pos, out var end))
        {
            return null;
        }

        if (pos + 16 + 4 > end)
        {
            return null;
        }

        pos += 16;
        pos += 4;
        return NifBinaryCursor.ReadSizedString(data, ref pos, end, nif.IsBigEndian);
    }

    private static bool TryReadShaderPropertyStart(
        byte[] data,
        BlockInfo propBlock,
        bool be,
        out int pos,
        out int end)
    {
        pos = propBlock.DataOffset;
        end = propBlock.DataOffset + propBlock.Size;

        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be))
        {
            return false;
        }

        if (pos + 2 > end)
        {
            return false;
        }

        pos += 2;
        return true;
    }

    private static string? ReadTextureSetSlot(
        byte[] data,
        BlockInfo block,
        bool be,
        int slotIndex)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (pos + 4 > end)
        {
            return null;
        }

        var numTextures = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numTextures <= (uint)slotIndex || numTextures > 20)
        {
            return null;
        }

        for (var i = 0; i < slotIndex; i++)
        {
            _ = NifBinaryCursor.ReadSizedString(data, ref pos, end, be);
            if (pos > end)
            {
                return null;
            }
        }

        return NifBinaryCursor.ReadSizedString(data, ref pos, end, be);
    }
}

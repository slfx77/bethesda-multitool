using System.Linq;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Reads shader property blocks to resolve texture set paths and shader flags.
/// </summary>
internal static class NifShaderTexturePropertyReader
{
    internal static NifShaderTextureMetadata? ReadShaderMetadata(
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

            if (!TryReadCommonShaderData(
                    data,
                    nif,
                    propBlock,
                    out var shaderType,
                    out var shaderFlags,
                    out var shaderFlags2,
                    out var envMapScale))
            {
                continue;
            }

            if (propBlock.TypeName == "BSShaderPPLightingProperty")
            {
                return new NifShaderTextureMetadata
                {
                    PropertyType = propBlock.TypeName,
                    ShaderType = shaderType,
                    ShaderFlags = shaderFlags,
                    ShaderFlags2 = shaderFlags2,
                    EnvMapScale = envMapScale,
                    TextureSlots = ReadTextureSetSlots(data, nif, propBlock)
                };
            }

            return new NifShaderTextureMetadata
            {
                PropertyType = propBlock.TypeName,
                ShaderType = shaderType,
                ShaderFlags = shaderFlags,
                ShaderFlags2 = shaderFlags2,
                EnvMapScale = envMapScale,
                TextureSlots = CreateFixedTextureSlots(
                    ResolveNoLightingTexture(data, nif, propBlock))
            };
        }

        return null;
    }

    internal static string? ResolveDiffusePath(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        return ReadShaderMetadata(data, nif, propertyRefs)?.DiffusePath;
    }

    internal static string? ResolveNormalMapPath(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        return ReadShaderMetadata(data, nif, propertyRefs)?.NormalMapPath;
    }

    internal static uint? ReadShaderFlags2(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return ReadShaderMetadata(data, nif, propertyRefs)?.ShaderFlags2;
    }

    internal static (uint ShaderFlags, uint ShaderFlags2)? ReadShaderFlagsBoth(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        var metadata = ReadShaderMetadata(data, nif, propertyRefs);
        if (metadata?.ShaderFlags == null || metadata.ShaderFlags2 == null)
        {
            return null;
        }

        return (metadata.ShaderFlags.Value, metadata.ShaderFlags2.Value);
    }

    internal static (uint ShaderFlags, float EnvMapScale)? ReadEnvMapInfo(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        var metadata = ReadShaderMetadata(data, nif, propertyRefs);
        if (metadata?.PropertyType != "BSShaderPPLightingProperty" ||
            metadata.ShaderFlags == null ||
            metadata.EnvMapScale == null)
        {
            return null;
        }

        return (metadata.ShaderFlags.Value, metadata.EnvMapScale.Value);
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

    private static bool TryReadCommonShaderData(
        byte[] data,
        NifInfo nif,
        BlockInfo propBlock,
        out uint shaderType,
        out uint shaderFlags,
        out uint shaderFlags2,
        out float envMapScale)
    {
        shaderType = 0;
        shaderFlags = 0;
        shaderFlags2 = 0;
        envMapScale = 0f;

        if (!TryReadShaderPropertyStart(data, propBlock, nif.IsBigEndian, out var pos, out var end) ||
            pos + 16 > end)
        {
            return false;
        }

        shaderType = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;
        shaderFlags = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;
        shaderFlags2 = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;
        envMapScale = BinaryUtils.ReadFloat(data, pos, nif.IsBigEndian);
        return true;
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

    private static List<string?> ReadTextureSetSlots(
        byte[] data,
        NifInfo nif,
        BlockInfo propBlock)
    {
        if (!TryReadTextureSetRef(data, nif, propBlock, out var textureSetRef) ||
            textureSetRef < 0 ||
            textureSetRef >= nif.Blocks.Count)
        {
            return [];
        }

        var textureSetBlock = nif.Blocks[textureSetRef];
        if (textureSetBlock.TypeName != "BSShaderTextureSet")
        {
            return CreateFixedTextureSlots();
        }

        return ReadTextureSetSlots(data, textureSetBlock, nif.IsBigEndian);
    }

    private static List<string?> ReadTextureSetSlots(
        byte[] data,
        BlockInfo block,
        bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (pos + 4 > end)
        {
            return CreateFixedTextureSlots();
        }

        var numTextures = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numTextures > 20)
        {
            return CreateFixedTextureSlots();
        }

        var slots = new List<string?>();
        for (var i = 0; i < numTextures; i++)
        {
            slots.Add(NifBinaryCursor.ReadSizedString(data, ref pos, end, be));
            if (pos > end)
            {
                return CreateFixedTextureSlots();
            }
        }

        return CreateFixedTextureSlots(slots);
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

    private static List<string?> CreateFixedTextureSlots(
        params string?[] slots)
    {
        return CreateFixedTextureSlots((IEnumerable<string?>)slots);
    }

    private static List<string?> CreateFixedTextureSlots(
        IEnumerable<string?> slots)
    {
        var fixedSlots = slots.Take(8).ToList();
        while (fixedSlots.Count < 8)
        {
            fixedSlots.Add(null);
        }

        return fixedSlots;
    }
}

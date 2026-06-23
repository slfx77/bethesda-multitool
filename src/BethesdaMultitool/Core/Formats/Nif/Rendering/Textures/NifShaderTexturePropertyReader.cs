using BethesdaMultitool.Core.Formats.Nif.Rendering.Parsing;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

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
            if (!TryGetPropertyBlock(nif, propRef, out var propBlock))
            {
                continue;
            }

            // Skyrim / Skyrim SE use BSLightingShaderProperty (a different layout from FO3/FNV's
            // BSShaderPPLightingProperty). Its texture set is reached after the NiObjectNET base +
            // Skyrim shader flags + UV transform; BSShaderTextureSet itself is identical across games.
            if (propBlock.TypeName == "BSLightingShaderProperty")
            {
                var slots = ReadBsLightingShaderTextureSlots(data, nif, propBlock);
                if (slots.Count == 0)
                {
                    continue;
                }

                return new NifShaderTextureMetadata
                {
                    PropertyType = propBlock.TypeName,
                    TextureSlots = slots
                };
            }

            // Skyrim / SE / FO4 effect shaders (fire, magic, glow, some glass/ice) use
            // BSEffectShaderProperty, whose texture is the inline "Source Texture" — NOT a
            // BSShaderTextureSet. Without this branch every effect shape resolved no diffuse and rendered
            // untextured (the "fire shows a floating normal map" look). FO76 (bsVer >= 155) instead names
            // a .bgem material file, handled by the same material-slot path as lighting shaders.
            if (propBlock.TypeName == "BSEffectShaderProperty")
            {
                var slots = nif.BsVersion >= 155
                    ? ReadFallout76MaterialSlot(data, nif, propBlock)
                    : ReadBsEffectShaderSourceTexture(data, nif, propBlock);
                if (slots.Count == 0)
                {
                    continue;
                }

                return new NifShaderTextureMetadata
                {
                    PropertyType = propBlock.TypeName,
                    TextureSlots = slots
                };
            }

            if (!IsShaderProperty(propBlock))
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
    {
        return propBlock.TypeName is
            "BSShaderPPLightingProperty" or
            "BSShaderNoLightingProperty";
    }

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

    /// <summary>
    ///     Read the texture-set slots of a Skyrim/Skyrim SE/Fallout 4 BSLightingShaderProperty.
    ///     Two layout subtleties vs the FO3/FNV BSShaderPPLightingProperty path:
    ///     <list type="number">
    ///         <item>
    ///             NiObjectNET prepends a 4-byte "Shader Type" field that exists ONLY for
    ///             BSLightingShaderProperty in Skyrim..FO4 (nif.xml <c>onlyT="BSLightingShaderProperty"</c>,
    ///             <c>#BS_GTE_SKY# #AND# #NI_BS_LTE_FO4#</c>) — skip it before walking NiObjectNET.
    ///         </item>
    ///         <item>
    ///             The NiShadeProperty "Flags" and FO3/FNV BSShaderProperty fields are absent here, so
    ///             after the NiObjectNET base come Shader Flags 1(4) + Shader Flags 2(4) + UV Offset(8)
    ///             + UV Scale(8) = +24, then the Texture Set ref.
    ///         </item>
    ///     </list>
    ///     (FO4/Starfield store materials in external BGSM/.mat files when "Name" is a path — not
    ///     handled here; the embedded texture set is still read when present.)
    /// </summary>
    private static List<string?> ReadBsLightingShaderTextureSlots(
        byte[] data,
        NifInfo nif,
        BlockInfo propBlock)
    {
        var pos = propBlock.DataOffset;
        var end = propBlock.DataOffset + propBlock.Size;

        // Fallout 76 (bsVer >= 155) has no inline Texture Set ref (nif.xml gates it to <= FO4) and no
        // leading "Shader Type" field. The shader's Name instead points at a .bgsm/.bgem material file
        // under materials\; hand that to the resolver as the diffuse slot — it parses the material and
        // resolves the real texture. (See NifTextureResolver.LoadFromMaterial.)
        if (nif.BsVersion >= 155)
        {
            return ReadFallout76MaterialSlot(data, nif, propBlock);
        }

        // Leading "Shader Type" field, present for BSLightingShaderProperty in Skyrim (bsVer 83)
        // through Fallout 4 (bsVer 130).
        if (nif.BsVersion is >= 83 and <= 130)
        {
            pos += 4;
        }

        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian))
        {
            return [];
        }

        pos += 24; // Shader Flags 1 + Shader Flags 2 + UV Offset + UV Scale

        var inlineSlots = ReadInlineTextureSet(data, nif, pos, end);

        // Fallout 4 (bsVer 130..<155) usually stores its material in an external .bgsm/.bgem referenced by
        // the shader's Name, leaving the inline texture set empty — which renders the shape white. When the
        // inline set yields no diffuse, fall back to the material Name. Unlike FO76 (no leading field), FO4
        // BSLightingShaderProperty prefixes a 4-byte "Shader Type", so the Name sits at DataOffset + 4.
        if (!HasDiffuse(inlineSlots) && nif.BsVersion is >= 130 and < 155)
        {
            var material = ReadMaterialNameSlot(data, nif, propBlock.DataOffset + 4);
            if (material.Count > 0)
            {
                return material;
            }
        }

        return inlineSlots;
    }

    /// <summary>
    ///     Fallout 76 BSLightingShaderProperty texture resolution: the NiObjectNET <c>Name</c> (a String
    ///     table index, since FO76 has no inline texture set) is a <c>.bgsm</c>/<c>.bgem</c> material
    ///     path. Returned as the diffuse slot so the resolver can parse the material and load its
    ///     textures. Returns empty when the Name isn't a material path.
    /// </summary>
    private static List<string?> ReadFallout76MaterialSlot(byte[] data, NifInfo nif, BlockInfo propBlock) =>
        ReadMaterialNameSlot(data, nif, propBlock.DataOffset);

    /// <summary>
    ///     Resolve a shader's material from its NiObjectNET <c>Name</c> (a String-table index) at
    ///     <paramref name="nameFieldOffset" />. The Name is a <c>.bgsm</c>/<c>.bgem</c> material path,
    ///     returned as the diffuse slot so the resolver can parse the material and load its real textures
    ///     (see NifTextureResolver.LoadFromMaterial). FO76 has no leading "Shader Type", so its Name sits at
    ///     the block data offset; FO4 BSLightingShaderProperty prefixes a 4-byte "Shader Type", so its Name
    ///     sits at offset + 4. Returns empty when the Name isn't a material path.
    /// </summary>
    private static List<string?> ReadMaterialNameSlot(byte[] data, NifInfo nif, int nameFieldOffset)
    {
        if (nameFieldOffset < 0 || nameFieldOffset + 4 > data.Length)
        {
            return [];
        }

        var nameIndex = BinaryUtils.ReadInt32(data, nameFieldOffset, nif.IsBigEndian);
        if (nameIndex < 0 || nameIndex >= nif.Strings.Count)
        {
            return [];
        }

        var name = nif.Strings[nameIndex];
        if (string.IsNullOrEmpty(name) ||
            (!name.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) &&
             !name.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        return CreateFixedTextureSlots(name);
    }

    /// <summary>
    ///     Read the inline Texture Set ref (Skyrim..FO4) at <paramref name="pos" /> and return its slots, or
    ///     an empty list when the ref is absent/invalid or doesn't point at a BSShaderTextureSet.
    /// </summary>
    private static List<string?> ReadInlineTextureSet(byte[] data, NifInfo nif, int pos, int end)
    {
        if (pos + 4 > end)
        {
            return [];
        }

        var textureSetRef = BinaryUtils.ReadInt32(data, pos, nif.IsBigEndian);
        if (textureSetRef < 0 || textureSetRef >= nif.Blocks.Count)
        {
            return [];
        }

        var textureSetBlock = nif.Blocks[textureSetRef];
        return textureSetBlock.TypeName == "BSShaderTextureSet"
            ? ReadTextureSetSlots(data, textureSetBlock, nif.IsBigEndian)
            : [];
    }

    /// <summary>True when the diffuse (slot 0) of a texture-slot list is populated.</summary>
    private static bool HasDiffuse(List<string?> slots) => slots.Count > 0 && !string.IsNullOrEmpty(slots[0]);

    /// <summary>
    ///     Reads the inline "Source Texture" of a Skyrim / SE / Fallout 4 BSEffectShaderProperty (the
    ///     effect's main texture — fire, magic, glow). After the NiObjectNET base come Shader Flags 1(4) +
    ///     Shader Flags 2(4) + UV Offset(8) + UV Scale(8) = +24, then the Source Texture SizedString.
    ///     Returned as the diffuse slot. Unlike BSLightingShaderProperty there is NO leading "Shader Type"
    ///     field (that's BSLightingShaderProperty-only per nif.xml), so the walk starts at NiObjectNET.
    /// </summary>
    private static List<string?> ReadBsEffectShaderSourceTexture(byte[] data, NifInfo nif, BlockInfo propBlock)
    {
        var pos = propBlock.DataOffset;
        var end = propBlock.DataOffset + propBlock.Size;

        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian))
        {
            return [];
        }

        pos += 24; // Shader Flags 1 + Shader Flags 2 + UV Offset + UV Scale
        if (pos + 4 > end)
        {
            return [];
        }

        var source = NifBinaryCursor.ReadSizedString(data, ref pos, end, nif.IsBigEndian);
        if (!string.IsNullOrEmpty(source))
        {
            return CreateFixedTextureSlots(source);
        }

        // FO4 (bsVer 130..<155) effect shaders can carry their texture via an external .bgem material in the
        // Name. BSEffectShaderProperty has no leading "Shader Type", so the Name is at the block data offset.
        return nif.BsVersion is >= 130 and < 155
            ? ReadMaterialNameSlot(data, nif, propBlock.DataOffset)
            : [];
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

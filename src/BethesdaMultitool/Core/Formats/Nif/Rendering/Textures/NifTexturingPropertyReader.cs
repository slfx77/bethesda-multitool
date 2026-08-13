using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Resolves the diffuse texture path from a legacy <c>NiTexturingProperty</c> by walking its
///     base map to the referenced <c>NiSourceTexture</c> and reading that block's file name.
///     <para>
///         Used as a fallback for meshes (e.g. <c>effects\ambient\fxvulturesNV.nif</c>) that texture
///         their geometry through a <c>NiTexturingProperty</c> instead of a <c>BSShader*</c> property.
///         Those are handled by <see cref="NifShaderTexturePropertyReader" />, which ignores
///         <c>NiTexturingProperty</c> and would otherwise leave the mesh untextured (white fallback).
///     </para>
///     The base-map walk mirrors <see cref="NifTextureAnimationEvaluator" />'s base-texture state
///     reader; the file name is a string-table index in these (Bethesda) NIFs, resolved against
///     <c>NifInfo.Strings</c> exactly like <c>NifObjectBlockReader.ReadBlockName</c>.
/// </summary>
internal static class NifTexturingPropertyReader
{
    internal static string? ResolveBaseTexturePath(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
            {
                continue;
            }

            var block = nif.Blocks[propRef];
            if (block.TypeName != "NiTexturingProperty")
            {
                continue;
            }

            if (TryReadBaseSourceRef(data, nif, block, out var sourceRef) &&
                TryReadSourceTextureFileName(data, nif, sourceRef, out var path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    ///     Reads the TES4-era NiTexturingProperty Apply Mode (a u32 on NIF &lt; 20.1.0.1). Oblivion's
    ///     renderer repurposes APPLY_HILIGHT (3) / APPLY_HILIGHT2 (4) as the PARALLAX markers — the
    ///     diffuse texture's alpha channel is a height map for those materials, not coverage.
    ///     Returns null for FO3+ NIFs (which store a flags ushort there instead) and for legacy
    ///     Morrowind NIFs (their apply modes keep the original NetImmerse meaning).
    /// </summary>
    internal static uint? ReadApplyMode(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        if (!nif.HasInlineStrings || NifVersions.IsLegacyNetImmerse(nif.BinaryVersion))
        {
            return null;
        }

        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
            {
                continue;
            }

            var block = nif.Blocks[propRef];
            if (block.TypeName != "NiTexturingProperty")
            {
                continue;
            }

            var pos = block.DataOffset;
            var end = block.DataOffset + block.Size;
            if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian, nif.HasInlineStrings, nif.BinaryVersion))
            {
                return null;
            }

            // NIF ≤ 10.0.1.2 carries a leading Flags ushort BEFORE Apply Mode (nif.xml gates both
            // fields there — same quirk NifRenderPropertyReader handles for NiMaterialProperty).
            if (nif.BinaryVersion <= NifVersions.Gamebryo10012)
            {
                pos += 2;
            }

            if (pos + 4 > end)
            {
                return null;
            }

            return BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
        }

        return null;
    }

    // Walks NiTexturingProperty (NiObjectNET + Flags(ushort) + Texture Count(uint) + Has Base
    // Texture(bool)) to the base map's TexDesc, whose first field is the NiSourceTexture ref.
    // Layout matches NifTextureAnimationEvaluator.TryReadBaseTextureState up to the source ref.
    private static bool TryReadBaseSourceRef(byte[] data, NifInfo nif, BlockInfo block, out int sourceRef)
    {
        sourceRef = -1;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian, nif.HasInlineStrings, nif.BinaryVersion))
        {
            return false;
        }

        if (NifVersions.IsLegacyNetImmerse(nif.BinaryVersion))
        {
            // Morrowind NiTexturingProperty: Flags (ushort) + Apply Mode (uint) + Texture Count (uint)
            // + Has Base Texture (32-bit bool) + Base Texture (TexDesc). The TexDesc's first field is
            // the NiSourceTexture ref, which is all we need.
            if (pos + 2 + 4 + 4 + 4 + 4 > end)
            {
                return false;
            }

            pos += 2; // Flags
            pos += 4; // Apply Mode
            pos += 4; // Texture Count
            var hasBase = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian) != 0;
            pos += 4;
            if (!hasBase)
            {
                return false;
            }

            sourceRef = BinaryUtils.ReadInt32(data, pos, nif.IsBigEndian);
            return sourceRef >= 0 && sourceRef < nif.Blocks.Count;
        }

        // After NiObjectNET, NiTexturingProperty has THREE era layouts (nif.xml 12469-12481):
        //   ≤ 10.0.1.2          — Flags (ushort) AND Apply Mode (uint) = 6 bytes. Oblivion's nine
        //                         default GroundCover* terrain grasses are authored at exactly
        //                         10.0.1.2; the old 4-byte skip read Has Base Texture out of the
        //                         middle of Texture Count (always 0x00) → no diffuse → the whole
        //                         default-grass set rendered as white translucent cards.
        //   10.0.1.3 – 20.1.0.1 — Apply Mode only (uint, 4 bytes; the rest of Oblivion).
        //   ≥ 20.1.0.2          — TexturingFlags (ushort, 2 bytes; FO3/FNV).
        // Then Texture Count (uint) + Has Base Texture (bool). Only (4.2.2.0, 10.0.1.2] reaches the
        // 6-byte arm — the legacy NetImmerse branch above handles Morrowind.
        int applyModeOrFlagsSize;
        if (nif.BinaryVersion <= NifVersions.Gamebryo10012)
        {
            applyModeOrFlagsSize = 6;
        }
        else if (nif.HasInlineStrings)
        {
            applyModeOrFlagsSize = 4;
        }
        else
        {
            applyModeOrFlagsSize = 2;
        }

        if (pos + applyModeOrFlagsSize + 4 + 1 > end)
        {
            return false;
        }

        pos += applyModeOrFlagsSize;
        pos += 4; // Texture Count
        var hasBaseTexture = data[pos] != 0;
        pos += 1;
        if (!hasBaseTexture || pos + 4 > end)
        {
            return false;
        }

        sourceRef = BinaryUtils.ReadInt32(data, pos, nif.IsBigEndian);
        return sourceRef >= 0 && sourceRef < nif.Blocks.Count;
    }

    // NiSourceTexture: NiObjectNET header + Use External(byte) + File Name. In FO3/FNV+ NIFs File Name
    // is a string-table index (resolved against NifInfo.Strings); in older ones (Oblivion/Morrowind)
    // it is an inline SizedString immediately after Use External.
    private static bool TryReadSourceTextureFileName(byte[] data, NifInfo nif, int sourceRef, out string? path)
    {
        path = null;
        var block = nif.Blocks[sourceRef];
        if (block.TypeName != "NiSourceTexture")
        {
            return false;
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian, nif.HasInlineStrings, nif.BinaryVersion))
        {
            return false;
        }

        if (pos + 1 > end)
        {
            return false;
        }

        pos += 1; // Use External (external textures store the file name as the next inline SizedString)

        if (nif.HasInlineStrings)
        {
            path = NifBinaryCursor.ReadSizedString(data, ref pos, end, nif.IsBigEndian);
            return !string.IsNullOrWhiteSpace(path);
        }

        if (pos + 4 > end)
        {
            return false;
        }

        var nameIndex = BinaryUtils.ReadInt32(data, pos, nif.IsBigEndian);
        if (nameIndex < 0 || nameIndex >= nif.Strings.Count)
        {
            return false;
        }

        path = nif.Strings[nameIndex];
        return !string.IsNullOrWhiteSpace(path);
    }
}

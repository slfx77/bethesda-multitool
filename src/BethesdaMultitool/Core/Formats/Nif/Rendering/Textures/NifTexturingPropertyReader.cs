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

        // After NiObjectNET, NiTexturingProperty has either Apply Mode (uint, NIF < 20.1.0.1 — Oblivion)
        // or TexturingFlags (ushort, NIF >= 20.1.0.2 — FO3/FNV), then Texture Count (uint) + Has Base
        // Texture (bool). HasInlineStrings (< 20.1.0.1) selects the 4-byte Apply Mode form.
        var applyModeOrFlagsSize = nif.HasInlineStrings ? 4 : 2;
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

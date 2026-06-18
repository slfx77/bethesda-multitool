// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using FalloutXbox360Utils.Core.Formats.Bsa.Ba2;
using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Indexed texture archive source backed by a BA2 (Fallout 4 / Fallout 76) archive. The BA2
///     parallel to <see cref="NifTextureArchiveSource" />: same <see cref="INifTextureSource" />
///     contract, so the resolver treats BSA- and BA2-sourced textures identically. DX10 entries are
///     extracted as full .dds (synthesized header + chunks) by <see cref="Ba2Extractor" />.
/// </summary>
internal sealed class Ba2TextureArchiveSource(
    Ba2Extractor extractor,
    Dictionary<string, Ba2FileRecord> fileIndex) : INifTextureSource
{
    public DecodedTexture? TryLoad(string path)
    {
        try
        {
            var rawData = TryLoadRaw(path);
            return rawData is null ? null : NifTextureLoader.DecodeTextureData(rawData);
        }
        catch
        {
            return null;
        }
    }

    public byte[]? TryLoadRaw(string path)
    {
        if (!fileIndex.TryGetValue(path, out var fileRecord))
        {
            return null;
        }

        try
        {
            return extractor.ExtractFile(fileRecord);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        extractor.Dispose();
    }
}

using System.Collections.Concurrent;
using System.Text;
using DDXConv;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Resolves and caches textures for NIF sprite rendering.
///     Parses BSShaderPPLightingProperty → BSShaderTextureSet chain to find diffuse texture paths,
///     then loads DDS textures from one or more textures BSAs.
///     Thread-safe: uses ConcurrentDictionary for texture cache.
/// </summary>
internal sealed class NifTextureResolver : IDisposable
{
    private readonly List<BsaSource> _sources = [];
    private readonly ConcurrentDictionary<string, DecodedTexture?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private int _cacheHits;
    private int _cacheMisses;

    public int CacheHits => _cacheHits;
    public int CacheMisses => _cacheMisses;

    public NifTextureResolver(params string[] texturesBsaPaths)
    {
        foreach (var bsaPath in texturesBsaPaths)
        {
            var archive = BsaParser.Parse(bsaPath);
            var extractor = new BsaExtractor(bsaPath);

            // Build O(1) file lookup index per BSA
            var fileIndex = new Dictionary<string, BsaFileRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in archive.AllFiles)
            {
                var path = file.FullPath;
                if (!string.IsNullOrEmpty(path))
                {
                    fileIndex[path.Replace('/', '\\')] = file;
                }
            }

            _sources.Add(new BsaSource(extractor, fileIndex));
        }
    }

    /// <summary>
    ///     Resolve the diffuse texture path from a shape's property block references.
    ///     Walks BSShaderPPLightingProperty → BSShaderTextureSet → slot 0 string,
    ///     or BSShaderNoLightingProperty → embedded filename.
    /// </summary>
    public static string? ResolveDiffusePath(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
            {
                continue;
            }

            var propBlock = nif.Blocks[propRef];

            if (propBlock.TypeName == "BSShaderPPLightingProperty")
            {
                return ResolvePPLightingTexture(data, nif, propBlock);
            }

            if (propBlock.TypeName == "BSShaderNoLightingProperty")
            {
                return ResolveNoLightingTexture(data, nif, propBlock);
            }
        }

        return null;
    }

    /// <summary>
    ///     Read BSShaderFlags2 from the first BSShaderPPLightingProperty in a shape's properties.
    ///     Returns null if no shader property found.
    /// </summary>
    public static uint? ReadShaderFlags2(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
                continue;

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName is "BSShaderPPLightingProperty" or "BSShaderNoLightingProperty")
            {
                var be = nif.IsBigEndian;
                var pos = propBlock.DataOffset;
                var end = propBlock.DataOffset + propBlock.Size;

                if (!SkipNiObjectNET(data, ref pos, end, be))
                    return null;

                // NiShadeProperty: Flags (ushort)
                if (pos + 2 > end) return null;
                pos += 2;

                // BSShaderProperty: ShaderType(4) + ShaderFlags(4) + ShaderFlags2(4)
                if (pos + 12 > end) return null;
                pos += 8; // skip ShaderType + ShaderFlags
                return BinaryUtils.ReadUInt32(data, pos, be);
            }
        }

        return null;
    }

    /// <summary>
    ///     Read both BSShaderFlags (word 0) and BSShaderFlags2 (word 1) from the first
    ///     BSShaderPPLightingProperty in a shape's properties.
    ///     Returns (ShaderFlags, ShaderFlags2) or null if no shader property found.
    /// </summary>
    public static (uint ShaderFlags, uint ShaderFlags2)? ReadShaderFlagsBoth(
        byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
                continue;

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName is "BSShaderPPLightingProperty" or "BSShaderNoLightingProperty")
            {
                var be = nif.IsBigEndian;
                var pos = propBlock.DataOffset;
                var end = propBlock.DataOffset + propBlock.Size;

                if (!SkipNiObjectNET(data, ref pos, end, be))
                    return null;

                // NiShadeProperty: Flags (ushort)
                if (pos + 2 > end) return null;
                pos += 2;

                // BSShaderProperty: ShaderType(4) + ShaderFlags(4) + ShaderFlags2(4)
                if (pos + 12 > end) return null;
                pos += 4; // skip ShaderType
                var flags1 = BinaryUtils.ReadUInt32(data, pos, be);
                pos += 4;
                var flags2 = BinaryUtils.ReadUInt32(data, pos, be);
                return (flags1, flags2);
            }
        }

        return null;
    }

    /// <summary>
    ///     Read BSShaderFlags (word 0) and EnvMapScale from BSShaderPPLightingProperty.
    ///     Used to detect Eye_Environment_Mapping (bit 17) and get the reflection strength.
    ///     Returns null if no BSShaderPPLightingProperty found.
    /// </summary>
    public static (uint ShaderFlags, float EnvMapScale)? ReadEnvMapInfo(
        byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
                continue;

            var propBlock = nif.Blocks[propRef];
            if (propBlock.TypeName != "BSShaderPPLightingProperty")
                continue;

            var be = nif.IsBigEndian;
            var pos = propBlock.DataOffset;
            var end = propBlock.DataOffset + propBlock.Size;

            if (!SkipNiObjectNET(data, ref pos, end, be))
                return null;

            // NiShadeProperty: Flags (ushort)
            if (pos + 2 > end) return null;
            pos += 2;

            // BSShaderProperty: ShaderType(4) + ShaderFlags(4) + ShaderFlags2(4) + EnvMapScale(4)
            if (pos + 16 > end) return null;
            pos += 4; // skip ShaderType
            var shaderFlags = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += 4; // skip ShaderFlags2
            var envMapScale = BinaryUtils.ReadFloat(data, pos, be);

            return (shaderFlags, envMapScale);
        }

        return null;
    }

    /// <summary>
    ///     Resolve the normal map texture path from a shape's property block references.
    ///     Walks BSShaderPPLightingProperty → BSShaderTextureSet → slot 1 string.
    /// </summary>
    public static string? ResolveNormalMapPath(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count)
            {
                continue;
            }

            var propBlock = nif.Blocks[propRef];

            if (propBlock.TypeName == "BSShaderPPLightingProperty")
            {
                return ResolvePPLightingNormalMap(data, nif, propBlock);
            }
        }

        return null;
    }

    /// <summary>
    ///     Injects a pre-built texture into the cache under the given path key.
    ///     Used for per-NPC EGT-morphed head textures that bypass normal BSA loading.
    ///     Thread-safe (overwrites any existing entry for the key).
    /// </summary>
    public void InjectTexture(string texturePath, DecodedTexture texture)
    {
        var normalized = NormalizePath(texturePath);
        _cache[normalized] = texture;
    }

    /// <summary>
    ///     Removes a previously injected texture from the CPU cache.
    ///     Used to free per-NPC morphed face textures after rendering.
    /// </summary>
    public void EvictTexture(string texturePath)
    {
        var normalized = NormalizePath(texturePath);
        _cache.TryRemove(normalized, out _);
    }

    /// <summary>
    ///     Load and cache a decoded texture by its BSA-relative path.
    ///     Thread-safe. Returns null if texture not found or unsupported format.
    /// </summary>
    public DecodedTexture? GetTexture(string texturePath)
    {
        var normalized = NormalizePath(texturePath);

        return _cache.GetOrAdd(normalized, path =>
        {
            Interlocked.Increment(ref _cacheMisses);

            var result = TryLoadFromSources(path);

            // Xbox 360 BSAs store textures as .ddx, but NIFs reference .dds —
            // the game engine maps the extension transparently at runtime.
            if (result == null && path.EndsWith(".dds", StringComparison.Ordinal))
            {
                var ddxPath = string.Concat(path.AsSpan(0, path.Length - 4), ".ddx");
                result = TryLoadFromSources(ddxPath);
            }

            return result;
        });
    }

    private DecodedTexture? TryLoadFromSources(string path)
    {
        foreach (var source in _sources)
        {
            if (!source.FileIndex.TryGetValue(path, out var fileRecord))
            {
                continue;
            }

            try
            {
                var rawData = source.Extractor.ExtractFile(fileRecord);
                var ddsData = ConvertDdxIfNeeded(rawData);
                return DdsTextureDecoder.Decode(ddsData);
            }
            catch
            {
                // Try next BSA
            }
        }

        return null;
    }

    /// <summary>
    ///     Record a cache hit (called externally since GetOrAdd doesn't distinguish hits).
    /// </summary>
    public void RecordCacheHit()
    {
        Interlocked.Increment(ref _cacheHits);
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Extractor.Dispose();
        }
    }

    private sealed record BsaSource(BsaExtractor Extractor, Dictionary<string, BsaFileRecord> FileIndex);

    /// <summary>
    ///     Parse BSShaderPPLightingProperty to find the Texture Set ref,
    ///     then read the BSShaderTextureSet's slot 0 string.
    /// </summary>
    private static string? ResolvePPLightingTexture(byte[] data, NifInfo nif, BlockInfo propBlock)
    {
        var be = nif.IsBigEndian;
        var pos = propBlock.DataOffset;
        var end = propBlock.DataOffset + propBlock.Size;

        // Skip NiObjectNET: Name(4) + NumExtraData(4) + refs(N*4) + Controller(4)
        if (!SkipNiObjectNET(data, ref pos, end, be))
        {
            return null;
        }

        // NiShadeProperty: Flags (ushort for FO3/FNV)
        if (pos + 2 > end) return null;
        pos += 2;

        // BSShaderProperty: ShaderType(4) + ShaderFlags(4) + ShaderFlags2(4) + EnvMapScale(4)
        if (pos + 16 > end) return null;
        pos += 16;

        // BSShaderLightingProperty: TextureClampMode(4)
        if (pos + 4 > end) return null;
        pos += 4;

        // BSShaderPPLightingProperty: Texture Set Ref (int32)
        if (pos + 4 > end) return null;
        var textureSetRef = BinaryUtils.ReadInt32(data, pos, be);

        if (textureSetRef < 0 || textureSetRef >= nif.Blocks.Count)
        {
            return null;
        }

        var tsBlock = nif.Blocks[textureSetRef];
        if (tsBlock.TypeName != "BSShaderTextureSet")
        {
            return null;
        }

        return ReadTextureSetSlot(data, tsBlock, be, 0);
    }

    /// <summary>
    ///     Parse BSShaderPPLightingProperty to find the Texture Set ref,
    ///     then read the BSShaderTextureSet's slot 1 string (normal map).
    /// </summary>
    private static string? ResolvePPLightingNormalMap(byte[] data, NifInfo nif, BlockInfo propBlock)
    {
        var be = nif.IsBigEndian;
        var pos = propBlock.DataOffset;
        var end = propBlock.DataOffset + propBlock.Size;

        if (!SkipNiObjectNET(data, ref pos, end, be))
        {
            return null;
        }

        // NiShadeProperty: Flags (ushort)
        if (pos + 2 > end) return null;
        pos += 2;

        // BSShaderProperty: ShaderType(4) + ShaderFlags(4) + ShaderFlags2(4) + EnvMapScale(4)
        if (pos + 16 > end) return null;
        pos += 16;

        // BSShaderLightingProperty: TextureClampMode(4)
        if (pos + 4 > end) return null;
        pos += 4;

        // BSShaderPPLightingProperty: Texture Set Ref (int32)
        if (pos + 4 > end) return null;
        var textureSetRef = BinaryUtils.ReadInt32(data, pos, be);

        if (textureSetRef < 0 || textureSetRef >= nif.Blocks.Count)
        {
            return null;
        }

        var tsBlock = nif.Blocks[textureSetRef];
        if (tsBlock.TypeName != "BSShaderTextureSet")
        {
            return null;
        }

        return ReadTextureSetSlot(data, tsBlock, be, 1);
    }

    /// <summary>
    ///     Parse BSShaderNoLightingProperty to read its embedded filename.
    /// </summary>
    private static string? ResolveNoLightingTexture(byte[] data, NifInfo nif, BlockInfo propBlock)
    {
        var be = nif.IsBigEndian;
        var pos = propBlock.DataOffset;
        var end = propBlock.DataOffset + propBlock.Size;

        // Skip NiObjectNET
        if (!SkipNiObjectNET(data, ref pos, end, be))
        {
            return null;
        }

        // NiShadeProperty: Flags (ushort)
        if (pos + 2 > end) return null;
        pos += 2;

        // BSShaderProperty: ShaderType(4) + ShaderFlags(4) + ShaderFlags2(4) + EnvMapScale(4)
        if (pos + 16 > end) return null;
        pos += 16;

        // BSShaderLightingProperty: TextureClampMode(4)
        if (pos + 4 > end) return null;
        pos += 4;

        // BSShaderNoLightingProperty: File Name (SizedString)
        return ReadSizedString(data, ref pos, end, be);
    }

    /// <summary>
    ///     Read a specific slot from a BSShaderTextureSet block.
    ///     Slot 0 = diffuse, slot 1 = normal map, etc.
    /// </summary>
    private static string? ReadTextureSetSlot(byte[] data, BlockInfo block, bool be, int slotIndex)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // NiObject has no fields — go straight to BSShaderTextureSet
        // NumTextures (uint32)
        if (pos + 4 > end) return null;
        var numTextures = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numTextures <= (uint)slotIndex || numTextures > 20)
        {
            return null;
        }

        // Skip preceding slots
        for (var i = 0; i < slotIndex; i++)
        {
            // Skip SizedString: read length, advance past it
            if (pos + 4 > end) return null;
            var skipLen = (int)BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += Math.Max(0, Math.Min(skipLen, 512));
            if (pos > end) return null;
        }

        // Read target slot SizedString
        return ReadSizedString(data, ref pos, end, be);
    }

    internal static bool SkipNiObjectNET(byte[] data, ref int pos, int end, bool be)
    {
        // Name (string index, int32)
        if (pos + 4 > end) return false;
        pos += 4;

        // NumExtraData (uint32) + refs
        if (pos + 4 > end) return false;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;

        // Controller ref (int32)
        if (pos + 4 > end) return false;
        pos += 4;

        return pos <= end;
    }

    private static string? ReadSizedString(byte[] data, ref int pos, int end, bool be)
    {
        if (pos + 4 > end) return null;
        var strLen = (int)BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (strLen <= 0 || strLen > 512 || pos + strLen > end)
        {
            pos += Math.Max(0, strLen);
            return null;
        }

        var result = Encoding.ASCII.GetString(data, pos, strLen);
        pos += strLen;

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>
    ///     If the data is a DDX texture (Xbox 360 format), convert it to DDS in memory.
    /// </summary>
    private static byte[] ConvertDdxIfNeeded(byte[] data)
    {
        if (data.Length < 4)
        {
            return data;
        }

        // Check for DDX magic bytes: "3XDO" or "3XDR"
        var is3Xdo = data[0] == '3' && data[1] == 'X' && data[2] == 'D' && data[3] == 'O';
        var is3Xdr = data[0] == '3' && data[1] == 'X' && data[2] == 'D' && data[3] == 'R';

        if (!is3Xdo && !is3Xdr)
        {
            return data;
        }

        try
        {
            var parser = new DdxParser();
            return parser.ConvertDdxToDds(data);
        }
        catch
        {
            // Fall through to return original data
        }

        return data;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant().Trim();

        // Some NIF paths lack the "textures\" prefix
        if (!normalized.StartsWith("textures\\", StringComparison.Ordinal))
        {
            normalized = "textures\\" + normalized;
        }

        return normalized;
    }
}

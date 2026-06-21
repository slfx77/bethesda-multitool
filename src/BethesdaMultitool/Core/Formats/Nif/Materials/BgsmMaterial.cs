// Copyright (c) 2026 BethesdaMultitool Contributors
// Licensed under the MIT License.
//
// Texture-path extraction from Bethesda material files (.bgsm = lighting, .bgem = effect), ported
// from fo76utils (MIT) src/bgsmfile.cpp. Fallout 4 and Fallout 76 NIFs don't carry an inline
// BSShaderTextureSet ref on their BSLightingShaderProperty; instead the shader's Name points at a
// material file under materials\ whose texture paths (diffuse, normal, ...) drive rendering.
//
// Only the texture paths are decoded here (everything the renderer needs); the material's physical
// parameters (specular/emissive/alpha/etc.) are intentionally skipped.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Nif.Materials;

/// <summary>
///     Parses the texture-path table of a Fallout 4 / Fallout 76 BGSM/BGEM material file. The slots
///     follow the BGSMFile convention (0 = diffuse, 1 = normal, 2 = glow, 3 = gradient, 4 = env map,
///     5 = env mask, 6 = specular, 7 = wrinkles, 8 = reflectance, 9 = lighting).
/// </summary>
public sealed class BgsmMaterial
{
    /// <summary>Diffuse / albedo texture slot.</summary>
    public const int SlotDiffuse = 0;

    /// <summary>Normal map slot.</summary>
    public const int SlotNormal = 1;

    private const int SlotCount = 10;

    private readonly string?[] _paths = new string?[SlotCount];

    private BgsmMaterial(byte version, bool isEffect)
    {
        Version = version;
        IsEffect = isEffect;
    }

    /// <summary>Material file version byte (2 = Fallout 4, 20–23 = Fallout 76).</summary>
    public byte Version { get; }

    /// <summary>True for an effect material (<c>.bgem</c> / BGEM magic), false for lighting (<c>.bgsm</c>).</summary>
    public bool IsEffect { get; }

    /// <summary>Diffuse texture path (archive-relative, as stored in the material), or null.</summary>
    public string? Diffuse => _paths[SlotDiffuse];

    /// <summary>Normal-map texture path, or null.</summary>
    public string? Normal => _paths[SlotNormal];

    /// <summary>Texture path for an arbitrary slot (0–9), or null.</summary>
    public string? GetTexturePath(int slot) => (uint)slot < SlotCount ? _paths[slot] : null;

    /// <summary>
    ///     Parses a BGSM/BGEM material's texture paths. Returns null if the buffer is not a recognized
    ///     Fallout 4 / Fallout 76 material file.
    /// </summary>
    public static BgsmMaterial? Parse(byte[] data)
    {
        if (data is null || data.Length < 64)
        {
            return null;
        }

        // Magic: "BGSM" (lighting) or "BGEM" (effect); the 3rd byte distinguishes them.
        if (data[0] != (byte)'B' || data[1] != (byte)'G' || data[3] != (byte)'M' ||
            (data[2] != (byte)'S' && data[2] != (byte)'E'))
        {
            return null;
        }

        var isEffect = data[2] == (byte)'E';
        var version = data[4];
        var isFallout4 = version == 2;
        var isFallout76 = (version & 0xFC) == 20; // 20..23
        if (!isFallout4 && !isFallout76)
        {
            return null; // v0 (material data embedded in the NIF) and Starfield .mat are out of scope
        }

        // The texture-path section position and the nibble-packed slot map are both version-gated,
        // and the "gradient map enabled" flag byte selects between two maps (per fo76utils bgsmfile.cpp).
        ulong texturePathMap;
        int pos;
        if (isFallout4)
        {
            var gradient = data[62] != 0;
            if (isEffect)
            {
                texturePathMap = gradient ? 0x00051430UL : 0x000514F0UL;
            }
            else
            {
                texturePathMap = gradient ? 0x0000000F7F243610UL : 0x0000000F7F244610UL;
            }

            pos = 63;
        }
        else
        {
            var gradient = data[58] != 0;
            if (isEffect)
            {
                texturePathMap = gradient ? 0xF9851430UL : 0xF98514F0UL;
                if (version >= 21)
                {
                    texturePathMap |= 0x000000FF00000000UL;
                }
            }
            else
            {
                texturePathMap = gradient ? 0x000000FF98723F10UL : 0x000000FF9872FF10UL;
            }

            pos = 60;
        }

        var material = new BgsmMaterial(version, isEffect);
        material.ReadTexturePaths(data, pos, texturePathMap);
        return material;
    }

    /// <summary>
    ///     Reads the texture-path strings. <paramref name="texturePathMap" /> packs the destination slot
    ///     numbers four bits at a time (low nibble first); a nibble ≥ 10 means "present but unused" — its
    ///     string is read and discarded. Each entry is a u32 length followed by the path bytes.
    /// </summary>
    private void ReadTexturePaths(byte[] data, int pos, ulong texturePathMap)
    {
        while (true)
        {
            var slot = (int)(texturePathMap & 0xF);
            if (pos + 4 > data.Length)
            {
                break;
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += 4;
            if (length < 0 || pos + length > data.Length)
            {
                break;
            }

            if (slot < SlotCount)
            {
                var path = ReadPath(data, pos, length);
                if (!string.IsNullOrEmpty(path))
                {
                    _paths[slot] = path;
                }
            }

            pos += length;
            texturePathMap >>= 4;
            if (texturePathMap == 0)
            {
                break;
            }
        }
    }

    private static string ReadPath(byte[] data, int pos, int length)
    {
        var end = pos;
        var limit = pos + length;
        while (end < limit && data[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, pos, end - pos).Trim();
    }
}

// Bethesda material file parser (.bgsm = lighting, .bgem = effect), ported from fo76utils (MIT)
// src/bgsmfile.cpp. Fallout 4 and Fallout 76 NIFs don't carry an inline BSShaderTextureSet ref on
// their BSLightingShaderProperty; instead the shader's Name points at a material file under
// materials\ whose texture paths (diffuse, normal, ...) drive rendering.
//
// Beyond texture paths, the render-state fields the engine gives material priority over the NIF's
// inline properties are decoded: the alpha block (opacity, blend enable + modes, test enable +
// threshold), two-sided, and — for lighting materials — specular color/strength/smoothness and
// emissive. Remaining physical params (UV tiling, env-map, decal, wrinkles, BGEM effect params)
// are intentionally skipped.

using System.Buffers.Binary;
using System.Numerics;
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

    /// <summary>Material opacity (engine-clamped 0–8; ≤ 1 in practice).</summary>
    public float Alpha { get; private set; } = 1f;

    /// <summary>Alpha blending enabled.</summary>
    public bool AlphaBlendEnabled { get; private set; }

    /// <summary>Source blend mode (NiAlphaProperty blend-mode enum, 0–10).</summary>
    public byte SourceBlendMode { get; private set; } = 6; // SRC_ALPHA

    /// <summary>Destination blend mode (NiAlphaProperty blend-mode enum, 0–10).</summary>
    public byte DestinationBlendMode { get; private set; } = 7; // INV_SRC_ALPHA

    /// <summary>Alpha testing (cutout) enabled; the engine tests GREATER against the threshold.</summary>
    public bool AlphaTestEnabled { get; private set; }

    /// <summary>Alpha-test reference value (0–255).</summary>
    public byte AlphaTestThreshold { get; private set; } = 128;

    /// <summary>Two-sided (backface culling disabled) — set on most foliage/wire materials.</summary>
    public bool TwoSided { get; private set; }

    /// <summary>Specular lighting enabled (lighting materials only).</summary>
    public bool SpecularEnabled { get; private set; }

    /// <summary>Specular color, each channel 0–1 (lighting materials only).</summary>
    public Vector3 SpecularColor { get; private set; }

    /// <summary>Specular strength/mult (0–8; FO76 stores 1 when enabled).</summary>
    public float SpecularStrength { get; private set; }

    /// <summary>Specular smoothness (0–8; ≤ 1 in practice — NOT an exponent).</summary>
    public float SpecularSmoothness { get; private set; }

    /// <summary>Emissive (glow) enabled (lighting materials only).</summary>
    public bool EmissiveEnabled { get; private set; }

    /// <summary>Emissive color RGB, each channel 0–1 (lighting materials only).</summary>
    public Vector3 EmissiveColor { get; private set; }

    /// <summary>Emissive scale (0–8).</summary>
    public float EmissiveScale { get; private set; }

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
        material.ReadAlphaBlock(data);
        var endPos = material.ReadTexturePaths(data, pos, texturePathMap);
        if (!isEffect)
        {
            material.ReadLightingParams(data, endPos, isFallout4);
        }

        return material;
    }

    /// <summary>
    ///     Reads the alpha/cull block from the fixed common header (identical layout in BGSM v2, BGSM
    ///     v20+, and BGEM per fo76utils loadBGSMFile): opacity f32 @0x1C, blend enable u8 @0x20, source
    ///     blend u32 @0x21, destination blend u32 @0x25, alpha-test threshold u8 @0x29, alpha-test enable
    ///     u8 @0x2A, then (skipping z-write/z-test/SSR u32 @0x2B and decal u8 @0x2F) two-sided u8 @0x30.
    /// </summary>
    private void ReadAlphaBlock(byte[] data)
    {
        Alpha = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0x1C)), 0f, 8f);
        AlphaBlendEnabled = data[0x20] != 0;
        SourceBlendMode = (byte)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x21)) & 15);
        DestinationBlendMode = (byte)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x25)) & 15);
        AlphaTestThreshold = data[0x29];
        AlphaTestEnabled = data[0x2A] != 0;
        TwoSided = data[0x30] != 0;
    }

    /// <summary>
    ///     Reads the specular + emissive section that follows the texture-path table in lighting (.bgsm)
    ///     materials (fo76utils loadBGSMFile lines 331-361): skip 15 (FO4) / 24 (FO76) enable-flag bytes,
    ///     u8 specular enable, f32×4 specular color RGB + strength, f32 smoothness; then skip 28/30 bytes,
    ///     the root-material string + 1 aniso byte, u8 emit enable → f32×4 emissive color RGB + scale.
    ///     Best-effort: leaves defaults on a short/odd buffer rather than failing the whole parse.
    /// </summary>
    private void ReadLightingParams(byte[] data, int pos, bool isFallout4)
    {
        pos += isFallout4 ? 15 : 24;
        if (pos + 21 > data.Length)
        {
            return;
        }

        var specularEnabled = data[pos] != 0;
        pos += 1;
        var r = ReadClamped01(data, pos);
        var g = ReadClamped01(data, pos + 4);
        var b = ReadClamped01(data, pos + 8);
        var strength = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos + 12)), 0f, 8f);
        pos += 16;
        var smoothness = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos)), 0f, 8f);
        pos += 4;
        SpecularEnabled = specularEnabled;
        SpecularColor = new Vector3(r, g, b);
        if (!specularEnabled)
        {
            SpecularStrength = 0f;
        }
        else
        {
            // FO76 stores a scale here but the engine treats enabled specular as strength 1 (fo76utils).
            SpecularStrength = isFallout4 ? strength : 1f;
        }

        SpecularSmoothness = smoothness;

        // Root-material string + anisotropic-lighting byte sit between specular and emissive.
        pos += isFallout4 ? 28 : 30;
        if (pos + 4 > data.Length)
        {
            return;
        }

        var rootLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;
        if (rootLength < 0 || pos + rootLength + 1 > data.Length)
        {
            return;
        }

        pos += rootLength + 1;
        if (pos + 17 > data.Length || data[pos] == 0)
        {
            return;
        }

        EmissiveEnabled = true;
        EmissiveColor = new Vector3(
            ReadClamped01(data, pos + 1), ReadClamped01(data, pos + 5), ReadClamped01(data, pos + 9));
        EmissiveScale = Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos + 13)), 0f, 8f);
    }

    private static float ReadClamped01(byte[] data, int pos) =>
        Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(pos)), 0f, 1f);

    /// <summary>
    ///     Reads the texture-path strings and returns the position just past the table. <paramref
    ///     name="texturePathMap" /> packs the destination slot numbers four bits at a time (low nibble
    ///     first); a nibble ≥ 10 means "present but unused" — its string is read and discarded. Each entry
    ///     is a u32 length followed by the path bytes.
    /// </summary>
    private int ReadTexturePaths(byte[] data, int pos, ulong texturePathMap)
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

        return pos;
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

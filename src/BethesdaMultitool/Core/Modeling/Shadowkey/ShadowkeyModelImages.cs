using System.Buffers.Binary;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The images and the palette of the Shadowkey documents (cut-2 plan decisions D3 and D10, sections 3.3 and 4.3):
///     mesh and sky skins as their exact 0x0RGB texel blocks with a lossless indexed PNG, zone <c>.ztx</c> textures as
///     their exact stored index bytes with an indexed PNG through the zone palette, and the zone palette itself as a
///     typed <see cref="ScenePalette" /> with the <c>.zlu</c> light table as its auxiliary table.
/// </summary>
/// <remarks>
///     <para>
///         Skins (D3): Original is the stored block (u16 little-endian 0x0RGB, top row first); the standard payload is an
///         8-bit indexed PNG whose PLTE lists the skin's distinct texels in first-appearance order with each 4-bit channel
///         times 17, which inverts exactly (the top nibble is clear on all 1,302,848 retail texels, at most 233 distinct
///         per skin). A skin with more than 256 colors (none on retail) gets an RGB truecolor PNG. No palette object is
///         declared for a skin: a direct-color source has none, and a fabricated one would claim a source object. The
///         magenta 0x0F0F texel stays opaque (the fog table does not pin it; diagnostic
///         <see cref="ShadowkeyModelDiagnostics.MagentaOpaque" />).
///     </para>
///     <para>
///         Zone textures (D10): Original is the 16,384 stored index bytes, bottom row first as the file stores them; the
///         standard payload is an indexed PNG, top row first, with the zone palette in PLTE and a tRNS only when the
///         palette declares its key; the image binds the palette through <see cref="SceneImageSource.PaletteIndex" />.
///     </para>
/// </remarks>
internal static class ShadowkeyModelImages
{
    /// <summary>The container label of a skin's original texel block.</summary>
    public const string SkinContainer = "shadowkey-skin-rgb444";

    /// <summary>The container label of a <c>.ztx</c> texture's original index bytes.</summary>
    public const string TextureContainer = "shadowkey-ztx-indices";

    /// <summary>The standard payload's container.</summary>
    public const string PngContainer = "png";

    /// <summary>The pixel-format namespace of the original descriptors.</summary>
    public const string PixelFormatNamespace = "bmt.shadowkey";

    /// <summary>The pixel-format code of a skin's texels.</summary>
    public const string SkinPixelFormat = "rgb444-le";

    /// <summary>The pixel-format code of a <c>.ztx</c> texture's indices.</summary>
    public const string TexturePixelFormat = "index8";

    /// <summary>The note of a skin's indexed PNG.</summary>
    public const string SkinNote = "0x0RGB 4:4:4 texels, nibble x17, indexed by first appearance";

    /// <summary>The note of a skin's truecolor PNG (more than 256 colors; none on retail).</summary>
    public const string SkinTruecolorNote = "0x0RGB 4:4:4 texels, nibble x17, RGB truecolor";

    /// <summary>The evidence of a skin's standard payload.</summary>
    public const string SkinEvidence =
        "top nibble clear on every texel of this skin; at most 256 distinct colors; x17 is invertible, so the PNG " +
        "reproduces every texel exactly";

    /// <summary>The note of a <c>.ztx</c> texture's indexed PNG.</summary>
    public const string TextureNote = "rows reversed: stored bottom row first; zone palette in PLTE";

    /// <summary>The evidence of a <c>.ztx</c> texture's standard payload.</summary>
    public const string TextureEvidence =
        "the 16,384 index bytes are the stored indices with the 128 rows in reverse order (ShadowkeyTextureBank, " +
        "settled by the orientable textures); PLTE is the zone .pal, 8-bit components";

    /// <summary>The color-space evidence of every Shadowkey image.</summary>
    public const string ColorSpaceEvidence =
        "N-Gage display colors; the engine applies no transfer function the files state (Assumed)";

    /// <summary>The expansion rule id of a zone palette.</summary>
    public const string PaletteExpansionRule = "shadowkey.pal.rgb8";

    /// <summary>The transparency rule id of a zone palette's key.</summary>
    public const string PaletteKeyRule = "shadowkey.pal.light-table-key";

    /// <summary>The interpretation rule id of the <c>.zlu</c> auxiliary table.</summary>
    public const string LightTableRule = "shadowkey.zlu.light-ramp";

    /// <summary>The auxiliary table's name and storage encoding.</summary>
    public const string LightTableName = "zlu";

    /// <summary>The auxiliary table's storage encoding.</summary>
    public const string LightTableEncoding = "u16le[4][64][256] 0x0RGB";

    /// <summary>
    ///     A skin's image: the original texel block and the lossless PNG (see the type remarks).
    /// </summary>
    /// <param name="name">The image name.</param>
    /// <param name="texels">The skin's texels (top row first).</param>
    /// <param name="stored">The stored texel block, exactly <c>2 x width x height</c> bytes.</param>
    /// <param name="width">The texture width.</param>
    /// <param name="height">The texture height.</param>
    /// <param name="location">The block's source location, or null.</param>
    public static SceneImage Skin(string name, IReadOnlyList<ushort> texels, ReadOnlySpan<byte> stored, int width,
        int height, SceneSourceLocation? location)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(texels);
        if (texels.Count != width * height || stored.Length != 2 * texels.Count)
        {
            throw new ArgumentException("A skin's texels and stored block must both cover width x height texels.",
                nameof(texels));
        }

        var descriptor = new SceneTextureDescriptor(width, height, pixelFormatNamespace: PixelFormatNamespace,
            pixelFormatCode: SkinPixelFormat, bitsPerPixel: 16, channels: SceneTextureChannels.Rgb,
            colorSpace: SceneColorSpace.Srgb, colorSpaceProvenance: SceneValueProvenance.Assumed,
            colorSpaceEvidence: ColorSpaceEvidence, alphaMeaning: SceneAlphaMeaning.Opaque,
            layout: "row-major, top row first, u16 little-endian 0x0RGB");
        SceneStandardImagePayload standard;
        if (TryFirstAppearancePalette(texels, out var palette, out var indices))
        {
            var png = ShadowkeyIndexedPng.EncodeIndexed(width, height, indices, palette);
            standard = new SceneStandardImagePayload(new SceneImagePayload(PngContainer, png),
                StandardDescriptor(width, height, 8, SceneTextureChannels.Indexed), SkinNote, SkinEvidence);
        }
        else
        {
            var rgb = new byte[texels.Count * 3];
            for (var i = 0; i < texels.Count; i++)
            {
                rgb[i * 3] = Expand((texels[i] >> 8) & 0xF);
                rgb[i * 3 + 1] = Expand((texels[i] >> 4) & 0xF);
                rgb[i * 3 + 2] = Expand(texels[i] & 0xF);
            }

            var png = ShadowkeyIndexedPng.EncodeRgb(width, height, rgb);
            standard = new SceneStandardImagePayload(new SceneImagePayload(PngContainer, png),
                StandardDescriptor(width, height, 24, SceneTextureChannels.Rgb), SkinTruecolorNote, SkinEvidence);
        }

        var source = new SceneImageSource(SkinContainer, descriptor, new SceneImagePayload(SkinContainer, stored),
            location, standard, origin: SceneImageOrigin.SourceReference);
        return new SceneImage(name, source);
    }

    /// <summary>
    ///     The skin's first-appearance palette (RGB, each channel times 17) and index image, or false when the skin holds
    ///     more than 256 distinct texels.
    /// </summary>
    public static bool TryFirstAppearancePalette(IReadOnlyList<ushort> texels, out byte[] palette, out byte[] indices)
    {
        ArgumentNullException.ThrowIfNull(texels);
        var lookup = new Dictionary<ushort, byte>(256);
        var colors = new List<ushort>(256);
        indices = new byte[texels.Count];
        for (var i = 0; i < texels.Count; i++)
        {
            if (!lookup.TryGetValue(texels[i], out var index))
            {
                if (colors.Count == 256)
                {
                    palette = [];
                    indices = [];
                    return false;
                }

                index = (byte)colors.Count;
                lookup.Add(texels[i], index);
                colors.Add(texels[i]);
            }

            indices[i] = index;
        }

        palette = new byte[colors.Count * 3];
        for (var i = 0; i < colors.Count; i++)
        {
            palette[i * 3] = Expand((colors[i] >> 8) & 0xF);
            palette[i * 3 + 1] = Expand((colors[i] >> 4) & 0xF);
            palette[i * 3 + 2] = Expand(colors[i] & 0xF);
        }

        return true;
    }

    /// <summary>True when any texel of the skin is the magenta 0x0F0F.</summary>
    public static bool HasMagenta(IReadOnlyList<ushort> texels)
    {
        ArgumentNullException.ThrowIfNull(texels);
        foreach (var texel in texels)
        {
            if (texel == ShadowkeyMesh.MagentaColourKey)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     One <c>.ztx</c> texture's image: the stored 16,384 index bytes (bottom row first) and the indexed PNG (top row
    ///     first) through the zone palette, bound to palette <paramref name="paletteIndex" />.
    /// </summary>
    /// <param name="name">The image name.</param>
    /// <param name="stored">The stored index bytes, bottom row first.</param>
    /// <param name="paletteRgb">The zone palette's 768 bytes.</param>
    /// <param name="transparentIndex">The declared key entry, or null.</param>
    /// <param name="paletteIndex">The zone palette's document index.</param>
    /// <param name="location">The stored bytes' location in the inflated <c>.ztx</c>, or null.</param>
    public static SceneImage Texture(string name, ReadOnlySpan<byte> stored, ReadOnlySpan<byte> paletteRgb,
        int? transparentIndex, int paletteIndex, SceneSourceLocation? location)
    {
        ArgumentNullException.ThrowIfNull(name);
        const int side = ShadowkeyTextureBank.TextureWidth;
        if (stored.Length != ShadowkeyTextureBank.TextureLength || paletteRgb.Length != ShadowkeyZonePalette.FileLength)
        {
            throw new ArgumentException("A .ztx texture is 128 x 128 indices through a 768-byte palette.", nameof(stored));
        }

        var topDown = TopDown(stored);
        byte[] alpha = [];
        if (transparentIndex is { } key)
        {
            alpha = new byte[key + 1];
            Array.Fill(alpha, (byte)255);
            alpha[key] = 0;
        }

        var png = ShadowkeyIndexedPng.EncodeIndexed(side, side, topDown, paletteRgb, alpha);
        var descriptor = new SceneTextureDescriptor(side, side, pixelFormatNamespace: PixelFormatNamespace,
            pixelFormatCode: TexturePixelFormat, bitsPerPixel: 8, channels: SceneTextureChannels.Indexed,
            colorSpace: SceneColorSpace.Srgb, colorSpaceProvenance: SceneValueProvenance.Assumed,
            colorSpaceEvidence: ColorSpaceEvidence,
            alphaMeaning: transparentIndex is null ? SceneAlphaMeaning.Opaque : SceneAlphaMeaning.Coverage,
            layout: "row-major, bottom row first, one palette index per texel");
        var standard = new SceneStandardImagePayload(new SceneImagePayload(PngContainer, png),
            StandardDescriptor(side, side, 8, SceneTextureChannels.Indexed), TextureNote, TextureEvidence);
        var source = new SceneImageSource(TextureContainer, descriptor, new SceneImagePayload(TextureContainer, stored),
            location, standard, origin: SceneImageOrigin.SourceReference, paletteIndex: paletteIndex);
        return new SceneImage(name, source);
    }

    /// <summary>The 128 stored rows reversed (the file stores the bottom row first).</summary>
    public static byte[] TopDown(ReadOnlySpan<byte> stored)
    {
        const int side = ShadowkeyTextureBank.TextureWidth;
        var topDown = new byte[stored.Length];
        for (var row = 0; row < ShadowkeyTextureBank.TextureHeight; row++)
        {
            stored.Slice(row * side, side).CopyTo(topDown.AsSpan((ShadowkeyTextureBank.TextureHeight - 1 - row) * side));
        }

        return topDown;
    }

    /// <summary>
    ///     The entry the palette keys: its first 0xFF00FF entry, declared only when the light table pins that entry to
    ///     0x0F0F at every bank and level (13 of 13 retail zones with magenta); null otherwise.
    /// </summary>
    public static int? KeyEntry(ReadOnlySpan<byte> paletteRgb, ReadOnlySpan<byte> lightTable)
    {
        if (ShadowkeyZonePalette.FindColourKeyIndex(paletteRgb) is not { } key ||
            lightTable.Length != ShadowkeyLightTable.PayloadLength)
        {
            return null;
        }

        for (var bank = 0; bank < ShadowkeyLightTable.Banks; bank++)
        {
            for (var level = 0; level < ShadowkeyLightTable.Levels; level++)
            {
                var at = ((bank * ShadowkeyLightTable.Levels + level) * ShadowkeyLightTable.PaletteEntries + key) * 2;
                if (BinaryPrimitives.ReadUInt16LittleEndian(lightTable[at..]) != ShadowkeyLightTable.ColourKeyEntry)
                {
                    return null;
                }
            }
        }

        return key;
    }

    /// <summary>
    ///     The zone palette (D10): Rgb8 entries with alpha 255, the <c>.zlu</c> as its auxiliary table, and the key entry
    ///     as its transparent index when <see cref="KeyEntry" /> declares one.
    /// </summary>
    /// <param name="stem">The zone stem (the palette's name and container are <c>&lt;stem&gt;.pal</c>).</param>
    /// <param name="paletteRgb">The 768 bytes of the <c>.pal</c>.</param>
    /// <param name="lightTable">The inflated <c>.zlu</c> (131,072 bytes).</param>
    /// <param name="paletteLocation">The <c>.pal</c> location, or null.</param>
    /// <param name="lightTableLocation">The inflated <c>.zlu</c> location, or null.</param>
    public static ScenePalette ZonePalette(string stem, ReadOnlySpan<byte> paletteRgb, ReadOnlySpan<byte> lightTable,
        SceneSourceLocation? paletteLocation, SceneSourceLocation? lightTableLocation)
    {
        ArgumentNullException.ThrowIfNull(stem);
        if (paletteRgb.Length != ShadowkeyZonePalette.FileLength)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"'{stem}.pal': a Shadowkey palette is {ShadowkeyZonePalette.FileLength} bytes, got {paletteRgb.Length}."));
        }

        var entries = new ScenePaletteEntry[paletteRgb.Length / 3];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new ScenePaletteEntry(paletteRgb[i * 3], paletteRgb[i * 3 + 1], paletteRgb[i * 3 + 2], 255);
        }

        var key = KeyEntry(paletteRgb, lightTable);
        var table = new ScenePaletteTable(LightTableName, LightTableEncoding, stem + ".zlu", lightTable,
            lightTableLocation, new ScenePaletteRule(LightTableRule, SceneValueProvenance.Assumed,
                "banks white/red/green/blue x 64 levels x 256 entries; ShadowkeyLightTable.Synthesize reproduces it " +
                "from the palette on 21 of 21 retail zones"));
        var container = stem + ".pal";
        return new ScenePalette(container, ScenePaletteEncoding.Rgb8, container, (ReadOnlyMemory<byte>?)paletteRgb.ToArray(), entries,
            new ScenePaletteRule(PaletteExpansionRule, SceneValueProvenance.Authored,
                "8-bit components used as stored; the maximum component is 255 in 21 of 21 retail palettes, so no " +
                "6-bit promotion applies"),
            paletteLocation,
            key is { } index ? new[] { index } : null,
            key is null
                ? null
                : new ScenePaletteRule(PaletteKeyRule, SceneValueProvenance.Assumed,
                    string.Create(CultureInfo.InvariantCulture,
                        $"the .zlu maps entry {key} to 0x0F0F at all 4 banks x 64 levels; entry {key} is the palette's " +
                        $"only magenta entry (13 of 13 retail zones with magenta)")),
            new[] { table });
    }

    private static SceneTextureDescriptor StandardDescriptor(int width, int height, int bitsPerPixel,
        SceneTextureChannels channels)
    {
        return new SceneTextureDescriptor(width, height, bitsPerPixel: bitsPerPixel, channels: channels,
            colorSpace: SceneColorSpace.Srgb, colorSpaceProvenance: SceneValueProvenance.Assumed,
            colorSpaceEvidence: ColorSpaceEvidence, layout: "row-major");
    }

    /// <summary>Replicates a 4-bit channel into 8 bits (<c>v x 17</c>).</summary>
    private static byte Expand(int value)
    {
        return (byte)(value * 17);
    }
}

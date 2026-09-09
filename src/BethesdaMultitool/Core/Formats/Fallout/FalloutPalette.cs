using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     Fallout's <c>COLOR.PAL</c> — the one palette every FRM sprite indexes. Measured on the
///     retail Fallout 1 archive 2026-09-05: 33,536 bytes, of which the first 768 are the palette
///     and the remaining 32,768 are a 32x32x32 RGB-to-index lookup cube the engine uses for
///     lighting, which nothing here needs.
///     <para>
///         ⚠⚠ The trap: the entries are <b>6-bit VGA</b>, but a range sniff sees 255 and calls the
///         file 8-bit, which renders everything four times too dark. The 255s are SENTINELS, not
///         colours — index 0 is <c>FF FF FF</c> and means transparent, and indices
///         <see cref="CycleStart" />..255 are <c>FF FF FF</c> placeholders the engine overwrites
///         each frame with its colour-cycling animation (fire, computer screens, water, radiation).
///         Exactly 27 entries carry the sentinel besides index 0, and every one of entries 1..228
///         is within the 6-bit range.
///     </para>
///     <para>
///         So this reader promotes 1..228 as 6-bit, makes index 0 transparent, and leaves the
///         cycled range at its sentinel white — those indices only appear on surfaces the engine
///         animates, and inventing a colour for them would be a guess. Animating them is a
///         separate, documented piece of work.
///     </para>
///     <para>
///         The footprint is measured over the 4,911 FRMs that actually use this palette — the 17
///         that ship their own are excluded, because there indices 229+ are ordinary colours and
///         counting them inflated an earlier figure to 0.72%. The real numbers: 0.21% of pixel
///         bytes, 84% of sprites never touching the range, and 61 (1.2%) spending more than 2% of
///         their bytes there — the fire and monitor art. The HUD
///         (<c>ART/INTRFACE/IFACE.FRM</c>) renders exactly, which pins the promotion as correct.
///     </para>
/// </summary>
internal static class FalloutPalette
{
    /// <summary>The global palette, used by everything that does not ship its own.</summary>
    public const string FileName = "COLOR.PAL";

    /// <summary>Bytes of the retail palette file: 768 palette + a 32x32x32 lookup cube.</summary>
    public const int RetailFileLength = Palette.RgbByteCount + LookupCubeLength;

    /// <summary>Bytes of the trailing RGB-to-index lookup cube (32 * 32 * 32).</summary>
    public const int LookupCubeLength = 32 * 32 * 32;

    /// <summary>First index of the runtime colour-cycling range.</summary>
    public const int CycleStart = 229;

    /// <summary>Highest component value a genuine 6-bit entry can hold.</summary>
    public const byte SixBitMaximum = 63;

    /// <summary>
    ///     Palette files to try for an image, best first. Fallout's full-screen slides each ship
    ///     their OWN palette beside the art under the same stem — <c>DEATH.FRM</c> takes
    ///     <c>DEATH.PAL</c>, and the ending sequence takes <c>SEQ*.PAL</c> — because 256 colours
    ///     cannot cover both a pre-rendered desert and the game's interface at once. 17 of the 18
    ///     per-image palettes in retail MASTER.DAT pair with an FRM of the same stem in the same
    ///     directory (the 18th, ART\CUTS\SUBTITLE.PAL, overlays cutscene video and has no FRM).
    ///     <para>
    ///         ⚠ Rendering a slide through COLOR.PAL is what a naive reader does, and it does not
    ///         merely shift the colours — DEATH.FRM's sky dithers between indices 104/118/201/239/244,
    ///         which DEATH.PAL maps to five near-identical pale blues but COLOR.PAL maps to two
    ///         blues and three colour-cycling whites, so the image renders as speckle. Mean
    ///         difference between horizontally adjacent pixels: 12.6 correct, 99.0 through COLOR.PAL.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> CandidatesFor(string imageFileName)
    {
        ArgumentNullException.ThrowIfNull(imageFileName);
        var stem = Path.GetFileNameWithoutExtension(imageFileName);
        return string.IsNullOrEmpty(stem) ? [FileName] : [stem + ".PAL", FileName];
    }

    /// <summary>
    ///     Reads a palette from <c>COLOR.PAL</c>'s bytes (the 768-byte block, or the whole retail
    ///     file). Index 0 is transparent; 1..228 are promoted from 6-bit.
    /// </summary>
    public static Palette Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length < Palette.RgbByteCount)
        {
            throw new InvalidDataException(
                $"'{name}' is {bytes.Length} bytes, shorter than the {Palette.RgbByteCount}-byte palette.");
        }

        var rgb = bytes[..Palette.RgbByteCount];

        // Promote only the entries that are really 6-bit; the sentinels would saturate anyway but
        // must not drag the whole file into an 8-bit reading.
        var promoted = new byte[Palette.RgbByteCount];
        for (var i = 0; i < Palette.EntryCount; i++)
        {
            var at = i * 3;
            if (IsSentinel(rgb, i))
            {
                promoted[at] = 255;
                promoted[at + 1] = 255;
                promoted[at + 2] = 255;
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                var v = rgb[at + c];
                if (v > SixBitMaximum)
                {
                    throw new InvalidDataException(
                        $"'{name}' entry {i} component {c} is {v}, above the 6-bit maximum {SixBitMaximum} and not a sentinel.");
                }

                // The same promotion Palette.FromVga6Bit uses, so endpoints land exactly.
                promoted[at + c] = (byte)((v << 2) | (v >> 4));
            }
        }

        return Palette.FromRgb8(promoted).WithTransparentIndex(0);
    }

    /// <summary>True when an entry is the <c>FF FF FF</c> sentinel rather than a colour.</summary>
    public static bool IsSentinel(ReadOnlySpan<byte> rgb768, int index)
    {
        var at = index * 3;
        return rgb768[at] == 255 && rgb768[at + 1] == 255 && rgb768[at + 2] == 255;
    }
}

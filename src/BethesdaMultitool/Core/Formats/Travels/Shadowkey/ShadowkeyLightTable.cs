using System.Buffers.Binary;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     A Shadowkey (N-Gage) <c>.zlu</c> light table: the zone's palette pre-multiplied by light
///     level and light colour, so the rasteriser can turn a texture index straight into a lit
///     RGB444 pixel. Little-endian, wrapped in the <see cref="ShadowkeyCompressedFile" /> envelope;
///     <see cref="Parse" /> takes the INFLATED payload, which is 131,072 bytes in all 21 retail
///     zones. The engine loads it at the step it labels "InitLevel Pre LUA" (its own label — these
///     are not Lua scripts; Shadowkey scripts are <c>.s</c> files, and what LUA expands to is
///     unknown).
///     <code>
///     u16[4][64][256]   value = 0x0RGB, 4 bits per channel
///        [bank][light level][palette index]
///     </code>
///     <para>
///         The whole table is synthesised from the zone's <c>.pal</c>. Reproduced here by
///         <see cref="Synthesize" /> with 0 mismatches over all 21 x 65,536 retail entries:
///         per channel, take the 8-bit palette component, keep it when the bank is 0 (white light)
///         or when it is that bank's dominant channel, otherwise tint it to <c>v * 2 / 5</c>
///         (integer, applied to the PALETTE and not to the scaled result — that order is what makes
///         the recipe exact, and no single linear multiplier fits at all), then scale by the light
///         level as <c>min(15, v * level / 408)</c>. The divisor 408 is (255/10)*16: light is
///         <c>level * 10</c> in 0..255 units, so level 25 is roughly unit brightness and level 63
///         is 2.47x — the table is a light AND glow ramp, not a 0..1 fade.
///     </para>
///     <para>
///         Bank 0 is white light, banks 1-3 are red-, green- and blue-tinted; that they serve
///         coloured light sources rather than screen effects is inference, not measurement.
///         Palette entries that are the 0xFF00FF colour key are pinned to 0x0F0F at every bank and
///         level, so the key survives lighting and can still be rejected afterwards. Because the
///         table depends only on the palette, <c>.zlu</c> identity mirrors <c>.pal</c> identity
///         exactly: 7 distinct palettes, 7 distinct tables, agreeing on all 210 zone pairs.
///     </para>
/// </summary>
internal sealed class ShadowkeyLightTable
{
    /// <summary>Light banks: 0 white, 1 red, 2 green, 3 blue.</summary>
    public const int Banks = 4;

    /// <summary>Light levels per bank.</summary>
    public const int Levels = 64;

    /// <summary>Palette entries per level.</summary>
    public const int PaletteEntries = Palette.EntryCount;

    /// <summary>u16 entries in the table.</summary>
    public const int EntryCount = Banks * Levels * PaletteEntries;

    /// <summary>Bytes in an inflated <c>.zlu</c> payload.</summary>
    public const int PayloadLength = EntryCount * 2;

    /// <summary>Largest value an RGB444 entry can hold.</summary>
    public const ushort MaxEntry = 0x0FFF;

    /// <summary>The divisor of the light ramp: (255 / 10) * 16.</summary>
    public const int LightDivisor = 408;

    /// <summary>The value every colour-key palette entry holds at every bank and level.</summary>
    public const ushort ColourKeyEntry = 0x0F0F;

    private readonly ushort[] _entries;

    private ShadowkeyLightTable(string name, ushort[] entries)
    {
        Name = name;
        _entries = entries;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The table, indexed <c>((bank * 64) + level) * 256 + paletteIndex</c>.</summary>
    public ReadOnlyMemory<ushort> Entries => _entries;

    /// <summary>
    ///     Parses an inflated <c>.zlu</c> payload. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when the payload is not
    ///     <see cref="PayloadLength" /> bytes or an entry has a bit set above the 12 an RGB444
    ///     colour uses.
    /// </summary>
    public static ShadowkeyLightTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length != PayloadLength)
        {
            throw new InvalidDataException(
                $"'{name}': a light table is {PayloadLength} bytes ({Banks} banks x {Levels} levels x {PaletteEntries} entries x 2), got {bytes.Length}.");
        }

        var entries = new ushort[EntryCount];
        for (var i = 0; i < entries.Length; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
            if (value > MaxEntry)
            {
                throw new InvalidDataException(
                    $"'{name}': entry at byte {i * 2} is 0x{value:X4}, past the 0x{MaxEntry:X4} an RGB444 colour can hold.");
            }

            entries[i] = value;
        }

        return new ShadowkeyLightTable(name, entries);
    }

    /// <summary>
    ///     Builds the table the recipe in the type remarks produces for
    ///     <paramref name="palette" />. Retail files match it byte for byte, so this doubles as the
    ///     round-trip check on the reading.
    /// </summary>
    public static ShadowkeyLightTable Synthesize(Palette palette, string name)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(name);

        var entries = new ushort[EntryCount];
        for (var bank = 0; bank < Banks; bank++)
        {
            for (var level = 0; level < Levels; level++)
            {
                for (var index = 0; index < PaletteEntries; index++)
                {
                    var (r, g, b, _) = palette.GetEntry(index);
                    ushort value;
                    if (r == 0xFF && g == 0x00 && b == 0xFF)
                    {
                        value = ColourKeyEntry;
                    }
                    else
                    {
                        var lit = Channel(r, 0, bank, level) << 8;
                        lit |= Channel(g, 1, bank, level) << 4;
                        lit |= Channel(b, 2, bank, level);
                        value = (ushort)lit;
                    }

                    entries[(bank * Levels + level) * PaletteEntries + index] = value;
                }
            }
        }

        return new ShadowkeyLightTable(name, entries);
    }

    /// <summary>Returns the lit RGB444 colour of one palette entry.</summary>
    public ushort Lookup(int bank, int level, int paletteIndex)
    {
        if (bank is < 0 or >= Banks)
        {
            throw new ArgumentOutOfRangeException(nameof(bank), bank, $"'{Name}': bank must be 0..{Banks - 1}.");
        }

        if (level is < 0 or >= Levels)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"'{Name}': level must be 0..{Levels - 1}.");
        }

        if (paletteIndex is < 0 or >= PaletteEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(paletteIndex), paletteIndex, $"'{Name}': palette index must be 0..{PaletteEntries - 1}.");
        }

        return _entries[(bank * Levels + level) * PaletteEntries + paletteIndex];
    }

    /// <summary>True when this table is byte-for-byte the one <see cref="Synthesize" /> builds.</summary>
    public bool MatchesPalette(Palette palette)
    {
        return Entries.Span.SequenceEqual(Synthesize(palette, Name).Entries.Span);
    }

    private static int Channel(byte component, int channel, int bank, int level)
    {
        var tinted = bank == 0 || channel == bank - 1 ? component : component * 2 / 5;
        return Math.Min(15, tinted * level / LightDivisor);
    }
}

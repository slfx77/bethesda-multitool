using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     A Shadowkey (N-Gage) <c>.zfg</c> fog table: every RGB444 colour blended towards the zone's
///     fog colour at each of 16 distances, so the rasteriser fogs a pixel with one lookup.
///     Little-endian, wrapped in the <see cref="ShadowkeyCompressedFile" /> envelope;
///     <see cref="Parse" /> takes the INFLATED payload, which is 131,072 bytes in all 21 retail
///     zones. The engine loads it at the step it labels "InitLevel Pre Fog".
///     <code>
///     u16[16][4096]   value = 0x0RGB, 4 bits per channel
///        [fog level][input colour 0x0RGB]
///     </code>
///     <para>
///         Every entry is the per-channel blend <c>(v * (16 - level) + fog * level) &gt;&gt; 4</c>,
///         reproduced by <see cref="Synthesize" /> with 0 mismatches over all 21 x 65,536 retail
///         entries. Level 0 is therefore the identity ramp in all 21 files, and level 15 is NOT a
///         constant row: the input still contributes 1/16, so a grey-fog zone's most-fogged row
///         holds several values a channel apart. That last property is what recovers the fog
///         colour exactly — at level 15 the entry for input 0xFFF is the fog colour itself, since
///         <c>(15 + 15f) &gt;&gt; 4 == f</c> for every f in 0..15.
///     </para>
///     <para>
///         Five distinct tables serve the 21 zones, and table identity is exactly fog-colour
///         identity, independent of the palette: black (0,0,0) in the 14 indoor/underground zones,
///         and blue-greys (10,10,11), (10,10,12), (9,9,11), (8,8,9) in the outdoor snow, mountain
///         and coast zones. No text file in the install carries the fog colour — the table is the
///         only place it appears.
///     </para>
/// </summary>
internal sealed class ShadowkeyFogTable
{
    /// <summary>Fog levels in the table.</summary>
    public const int Levels = 16;

    /// <summary>Input colours per level: every RGB444 value.</summary>
    public const int Colours = 4096;

    /// <summary>u16 entries in the table.</summary>
    public const int EntryCount = Levels * Colours;

    /// <summary>Bytes in an inflated <c>.zfg</c> payload.</summary>
    public const int PayloadLength = EntryCount * 2;

    /// <summary>Largest value an RGB444 entry can hold.</summary>
    public const ushort MaxEntry = 0x0FFF;

    private readonly ushort[] _entries;

    private ShadowkeyFogTable(string name, ushort[] entries, int fogR, int fogG, int fogB)
    {
        Name = name;
        _entries = entries;
        FogRed = fogR;
        FogGreen = fogG;
        FogBlue = fogB;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The table, indexed <c>(level * 4096) + rgb444</c>.</summary>
    public ReadOnlyMemory<ushort> Entries => _entries;

    /// <summary>The zone's fog colour, red channel, 0..15.</summary>
    public int FogRed { get; }

    /// <summary>The zone's fog colour, green channel, 0..15.</summary>
    public int FogGreen { get; }

    /// <summary>The zone's fog colour, blue channel, 0..15.</summary>
    public int FogBlue { get; }

    /// <summary>The fog colour packed as one RGB444 word.</summary>
    public ushort FogColour => (ushort)((FogRed << 8) | (FogGreen << 4) | FogBlue);

    /// <summary>True when level 0 maps every colour to itself, as in all 21 retail files.</summary>
    public bool IsLevelZeroIdentity { get; private init; }

    /// <summary>
    ///     True when the whole table is the blend of <see cref="FogColour" /> that
    ///     <see cref="Synthesize" /> builds — true for all 21 retail files. Reported rather than
    ///     enforced, so a table built some other way still reads.
    /// </summary>
    public bool MatchesBlendRecipe { get; private init; }

    /// <summary>
    ///     Parses an inflated <c>.zfg</c> payload. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when the payload is not
    ///     <see cref="PayloadLength" /> bytes or an entry has a bit set above the 12 an RGB444
    ///     colour uses.
    /// </summary>
    public static ShadowkeyFogTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length != PayloadLength)
        {
            throw new InvalidDataException(
                $"'{name}': a fog table is {PayloadLength} bytes ({Levels} levels x {Colours} colours x 2), got {bytes.Length}.");
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

        // At the highest level the fully saturated input still contributes 1/16, which makes
        // entry[15][0xFFF] equal the fog colour exactly rather than approximately.
        var recovered = entries[((Levels - 1) * Colours) + MaxEntry];
        var fogR = (recovered >> 8) & 0xF;
        var fogG = (recovered >> 4) & 0xF;
        var fogB = recovered & 0xF;

        var identity = true;
        for (var colour = 0; colour < Colours && identity; colour++)
        {
            identity = entries[colour] == colour;
        }

        var synthesized = Synthesize(fogR, fogG, fogB, name);
        return new ShadowkeyFogTable(name, entries, fogR, fogG, fogB)
        {
            IsLevelZeroIdentity = identity,
            MatchesBlendRecipe = synthesized.Entries.Span.SequenceEqual(entries),
        };
    }

    /// <summary>
    ///     Builds the table the blend in the type remarks produces for one 4-bit fog colour.
    ///     Retail files match it byte for byte.
    /// </summary>
    public static ShadowkeyFogTable Synthesize(int fogR, int fogG, int fogB, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(fogR);
        ArgumentOutOfRangeException.ThrowIfNegative(fogG);
        ArgumentOutOfRangeException.ThrowIfNegative(fogB);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fogR, 15);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fogG, 15);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fogB, 15);

        var entries = new ushort[EntryCount];
        for (var level = 0; level < Levels; level++)
        {
            for (var colour = 0; colour < Colours; colour++)
            {
                var blended = Blend((colour >> 8) & 0xF, fogR, level) << 8;
                blended |= Blend((colour >> 4) & 0xF, fogG, level) << 4;
                blended |= Blend(colour & 0xF, fogB, level);
                entries[(level * Colours) + colour] = (ushort)blended;
            }
        }

        return new ShadowkeyFogTable(name, entries, fogR, fogG, fogB)
        {
            IsLevelZeroIdentity = true,
            MatchesBlendRecipe = true,
        };
    }

    /// <summary>Returns the fogged colour of <paramref name="rgb444" /> at <paramref name="level" />.</summary>
    public ushort Lookup(int level, int rgb444)
    {
        if (level is < 0 or >= Levels)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, $"'{Name}': level must be 0..{Levels - 1}.");
        }

        if (rgb444 is < 0 or >= Colours)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rgb444), rgb444, $"'{Name}': an RGB444 colour is 0..{Colours - 1}.");
        }

        return _entries[(level * Colours) + rgb444];
    }

    private static int Blend(int value, int fog, int level)
    {
        return ((value * (Levels - level)) + (fog * level)) >> 4;
    }
}

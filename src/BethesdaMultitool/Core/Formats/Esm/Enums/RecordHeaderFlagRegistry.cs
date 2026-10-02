using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Enums;

/// <summary>
///     Names for the bits of a main record's header flags (the u32 at record-header offset 8), per
///     game and per record signature. The same bit means different things on different signatures:
///     bit 10 is Persistent on ACHR/REFR/CELL, Quest Item on items and base objects, and Displays In
///     Main Menu on LSCR; bit 11 is Initially Disabled on placed references and NAVM and has no
///     meaning on QUST. A flat, signature-less table therefore names bits wrongly.
///     <para>
///         Common to every TES4-framed game (<see cref="EngineFamily.Tes4" />, which includes the
///         <see cref="BethesdaGame.Unknown" /> profile): bit 5 Deleted, bit 12 Ignored, bit 18
///         Compressed. These agree with the <see cref="Models.DetectedMainRecord" /> and
///         <see cref="Parsing.MainRecordHeader" /> predicates (0x20, 0x1000, 0x40000), and the
///         per-signature tables agree with their 0x400 Persistent and 0x800 Initially Disabled on the
///         signatures where those bits carry that meaning. Morrowind (TES3 framing) and the pre-plugin
///         games name nothing: their headers are not this layout.
///     </para>
///     <para>
///         Per-signature names exist for Fallout 3 and Fallout: New Vegas only, selected by an
///         explicit <see cref="BethesdaGame" /> switch; every other TES4-framed game gets the common
///         bits. The names are transcribed from xEdit's record definitions
///         (<c>Sample/Reference_Code/TES5Edit/Core/wbDefinitionsFNV.pas</c> and
///         <c>wbDefinitionsFO3.pas</c>, the flag list of each <c>wbRecord</c>/<c>wbRefRecord</c>
///         call; <c>wbInterface.pas</c> <c>wbFlagsList</c> adds Deleted and Ignored to every list).
///         xEdit is MPL-2.0 and is used here only as a format reference: the bit names are facts,
///         and no code is copied. The two games' lists are identical except that Fallout 3 has no
///         TREE list and spells NAVM bit 26 "Autogen" where New Vegas spells it "AutoGen".
///         xEdit's placeholder "Unknown N" entries (INFO 13, WEAP 27/29, PACK 27, PROJ 27, CREA 19/29,
///         NPC_ 19, ALCH 29) stay unnamed.
///     </para>
/// </summary>
public static class RecordHeaderFlagRegistry
{
    private static readonly FlagBit[] CommonBits =
    [
        Bit(5, "Deleted"),
        Bit(12, "Ignored"),
        Bit(18, "Compressed")
    ];

    private static readonly IReadOnlyDictionary<string, FlagBit[]> NoSignatureBits =
        new Dictionary<string, FlagBit[]>(StringComparer.Ordinal);

    // Transcribed from wbDefinitionsFNV.pas (35 record flag lists).
    private static readonly IReadOnlyDictionary<string, FlagBit[]> FalloutNewVegasBits =
        new Dictionary<string, FlagBit[]>(StringComparer.Ordinal)
        {
            ["ACHR"] = [Bit(10, "Persistent"), Bit(11, "Initially Disabled"), Bit(25, "No AI Acquire")],
            ["ACRE"] =
            [
                Bit(10, "Persistent"), Bit(11, "Initially Disabled"), Bit(15, "Visible When Distant"),
                Bit(25, "No AI Acquire")
            ],
            ["ACTI"] =
            [
                Bit(6, "Has Tree LOD"), Bit(9, "On Local Map"), Bit(10, "Quest Item"),
                Bit(15, "Visible When Distant"), Bit(16, "Random Anim Start"), Bit(17, "Dangerous"),
                Bit(19, "Has Platform Specific Textures"), Bit(25, "Obstacle"), Bit(26, "Navmesh - Filter"),
                Bit(27, "Navmesh - Bounding Box"), Bit(29, "Child Can Use"), Bit(30, "Navmesh - Ground")
            ],
            ["ALCH"] = [Bit(10, "Quest Item")],
            ["ARMO"] = [Bit(10, "Quest Item"), Bit(19, "Has Platform Specific Textures")],
            ["BOOK"] = [Bit(10, "Quest Item")],
            ["CELL"] = [Bit(10, "Persistent"), Bit(17, "Off Limits"), Bit(19, "Can't Wait")],
            ["CONT"] =
            [
                Bit(10, "Quest Item"), Bit(16, "Random Anim Start"), Bit(25, "Obstacle"),
                Bit(26, "Navmesh - Filter"), Bit(27, "Navmesh - Bounding Box"), Bit(30, "Navmesh - Ground")
            ],
            ["CREA"] = [Bit(10, "Quest Item")],
            ["DOOR"] = [Bit(10, "Quest Item"), Bit(15, "Visible When Distant"), Bit(16, "Random Anim Start")],
            ["FURN"] = [Bit(10, "Quest Item"), Bit(16, "Random Anim Start"), Bit(29, "Child Can Use")],
            ["GLOB"] = [Bit(6, "Constant")],
            ["IDLM"] = [Bit(10, "Quest Item"), Bit(29, "Child Can Use")],
            ["KEYM"] = [Bit(10, "Quest Item")],
            ["LAND"] = [Bit(18, "Compressed")],
            ["LIGH"] = [Bit(10, "Quest Item"), Bit(16, "Random Anim Start"), Bit(25, "Obstacle")],
            ["LSCR"] = [Bit(10, "Displays In Main Menu")],
            ["MISC"] = [Bit(10, "Quest Item")],
            ["MSTT"] =
            [
                Bit(9, "On Local Map"), Bit(10, "Quest Item"), Bit(16, "Random Anim Start"), Bit(25, "Obstacle")
            ],
            ["NAVM"] = [Bit(11, "Initially Disabled"), Bit(26, "AutoGen")],
            ["NPC_"] = [Bit(10, "Quest Item"), Bit(18, "Compressed")],
            ["PGRE"] = [Bit(10, "Persistent"), Bit(11, "Initially Disabled")],
            ["REFR"] =
            [
                Bit(6, "Hidden From Local Map"), Bit(7, "Turn Off Fire"), Bit(8, "Inaccessible"),
                Bit(9, "Casts Shadows/Motion Blur"), Bit(10, "Persistent"), Bit(11, "Initially Disabled"),
                Bit(15, "Visible When Distant"), Bit(16, "High Priority LOD"), Bit(25, "No AI Acquire"),
                Bit(26, "Navmesh - Filter"), Bit(27, "Navmesh - Bounding Box"), Bit(28, "Reflected By Auto Water"),
                Bit(29, "Refracted by Auto Water"), Bit(30, "Navmesh - Ground"), Bit(31, "Multibound")
            ],
            ["REGN"] = [Bit(6, "Border Region")],
            ["SCOL"] =
            [
                Bit(6, "Has Tree LOD"), Bit(9, "On Local Map"), Bit(10, "Quest Item"),
                Bit(15, "Visible When Distant"), Bit(25, "Obstacle"), Bit(26, "Navmesh - Filter"),
                Bit(27, "Navmesh - Bounding Box"), Bit(30, "Navmesh - Ground")
            ],
            ["STAT"] =
            [
                Bit(6, "Has Tree LOD"), Bit(9, "On Local Map"), Bit(10, "Quest Item"),
                Bit(15, "Visible When Distant"), Bit(25, "Obstacle"), Bit(26, "Navmesh - Filter"),
                Bit(27, "Navmesh - Bounding Box"), Bit(30, "Navmesh - Ground")
            ],
            ["TACT"] =
            [
                Bit(9, "On Local Map"), Bit(10, "Quest Item"), Bit(13, "No Voice Filter"),
                Bit(16, "Random Anim Start"), Bit(17, "Radio Station"), Bit(28, "Non-Pipboy"),
                Bit(30, "Cont. Broadcast")
            ],
            ["TERM"] = [Bit(10, "Quest Item"), Bit(16, "Random Anim Start")],
            ["TES4"] = [Bit(0, "ESM"), Bit(4, "Optimized")],
            ["TREE"] = [Bit(6, "Has Tree LOD")],
            ["WEAP"] = [Bit(10, "Quest Item")],
            ["WRLD"] = [Bit(19, "Can't Wait")]
        };

    // Transcribed from wbDefinitionsFO3.pas (34 record flag lists). Kept as its own table rather than
    // derived from New Vegas's: the per-game-only rule, and the lists do differ (no TREE list here;
    // NAVM bit 26 is spelled "Autogen").
    private static readonly IReadOnlyDictionary<string, FlagBit[]> Fallout3Bits =
        new Dictionary<string, FlagBit[]>(StringComparer.Ordinal)
        {
            ["ACHR"] = [Bit(10, "Persistent"), Bit(11, "Initially Disabled"), Bit(25, "No AI Acquire")],
            ["ACRE"] =
            [
                Bit(10, "Persistent"), Bit(11, "Initially Disabled"), Bit(15, "Visible When Distant"),
                Bit(25, "No AI Acquire")
            ],
            ["ACTI"] =
            [
                Bit(6, "Has Tree LOD"), Bit(9, "On Local Map"), Bit(10, "Quest Item"),
                Bit(15, "Visible When Distant"), Bit(16, "Random Anim Start"), Bit(17, "Dangerous"),
                Bit(19, "Has Platform Specific Textures"), Bit(25, "Obstacle"), Bit(26, "Navmesh - Filter"),
                Bit(27, "Navmesh - Bounding Box"), Bit(29, "Child Can Use"), Bit(30, "Navmesh - Ground")
            ],
            ["ALCH"] = [Bit(10, "Quest Item")],
            ["ARMO"] = [Bit(10, "Quest Item"), Bit(19, "Has Platform Specific Textures")],
            ["BOOK"] = [Bit(10, "Quest Item")],
            ["CELL"] = [Bit(10, "Persistent"), Bit(17, "Off Limits"), Bit(19, "Can't Wait")],
            ["CONT"] =
            [
                Bit(10, "Quest Item"), Bit(16, "Random Anim Start"), Bit(25, "Obstacle"),
                Bit(26, "Navmesh - Filter"), Bit(27, "Navmesh - Bounding Box"), Bit(30, "Navmesh - Ground")
            ],
            ["CREA"] = [Bit(10, "Quest Item")],
            ["DOOR"] = [Bit(10, "Quest Item"), Bit(15, "Visible When Distant"), Bit(16, "Random Anim Start")],
            ["FURN"] = [Bit(10, "Quest Item"), Bit(16, "Random Anim Start"), Bit(29, "Child Can Use")],
            ["GLOB"] = [Bit(6, "Constant")],
            ["IDLM"] = [Bit(10, "Quest Item"), Bit(29, "Child Can Use")],
            ["KEYM"] = [Bit(10, "Quest Item")],
            ["LAND"] = [Bit(18, "Compressed")],
            ["LIGH"] = [Bit(10, "Quest Item"), Bit(16, "Random Anim Start"), Bit(25, "Obstacle")],
            ["LSCR"] = [Bit(10, "Displays In Main Menu")],
            ["MISC"] = [Bit(10, "Quest Item")],
            ["MSTT"] =
            [
                Bit(9, "On Local Map"), Bit(10, "Quest Item"), Bit(16, "Random Anim Start"), Bit(25, "Obstacle")
            ],
            ["NAVM"] = [Bit(11, "Initially Disabled"), Bit(26, "Autogen")],
            ["NPC_"] = [Bit(10, "Quest Item"), Bit(18, "Compressed")],
            ["PGRE"] = [Bit(10, "Persistent"), Bit(11, "Initially Disabled")],
            ["REFR"] =
            [
                Bit(6, "Hidden From Local Map"), Bit(7, "Turn Off Fire"), Bit(8, "Inaccessible"),
                Bit(9, "Casts Shadows/Motion Blur"), Bit(10, "Persistent"), Bit(11, "Initially Disabled"),
                Bit(15, "Visible When Distant"), Bit(16, "High Priority LOD"), Bit(25, "No AI Acquire"),
                Bit(26, "Navmesh - Filter"), Bit(27, "Navmesh - Bounding Box"), Bit(28, "Reflected By Auto Water"),
                Bit(29, "Refracted by Auto Water"), Bit(30, "Navmesh - Ground"), Bit(31, "Multibound")
            ],
            ["REGN"] = [Bit(6, "Border Region")],
            ["SCOL"] =
            [
                Bit(6, "Has Tree LOD"), Bit(9, "On Local Map"), Bit(10, "Quest Item"),
                Bit(15, "Visible When Distant"), Bit(25, "Obstacle"), Bit(26, "Navmesh - Filter"),
                Bit(27, "Navmesh - Bounding Box"), Bit(30, "Navmesh - Ground")
            ],
            ["STAT"] =
            [
                Bit(6, "Has Tree LOD"), Bit(9, "On Local Map"), Bit(10, "Quest Item"),
                Bit(15, "Visible When Distant"), Bit(25, "Obstacle"), Bit(26, "Navmesh - Filter"),
                Bit(27, "Navmesh - Bounding Box"), Bit(30, "Navmesh - Ground")
            ],
            ["TACT"] =
            [
                Bit(9, "On Local Map"), Bit(10, "Quest Item"), Bit(13, "No Voice Filter"),
                Bit(16, "Random Anim Start"), Bit(17, "Radio Station"), Bit(28, "Non-Pipboy"),
                Bit(30, "Cont. Broadcast")
            ],
            ["TERM"] = [Bit(10, "Quest Item"), Bit(16, "Random Anim Start")],
            ["TES4"] = [Bit(0, "ESM"), Bit(4, "Optimized")],
            ["WEAP"] = [Bit(10, "Quest Item")],
            ["WRLD"] = [Bit(19, "Can't Wait")]
        };

    /// <summary>
    ///     The name of header-flag <paramref name="bit" /> (0 = least significant) on a
    ///     <paramref name="signature" /> record of <paramref name="game" />, or <c>null</c> when the bit
    ///     has no known meaning there (including xEdit's "Unknown N" placeholders).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bit" /> is outside 0..31.</exception>
    public static string? GetName(BethesdaGame game, string signature, int bit)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, 31);

        if (GameProfiles.For(game).Engine != EngineFamily.Tes4)
        {
            return null;
        }

        var mask = 1u << bit;

        // wbFlagsList injects Deleted and Ignored ahead of each record's own list; no transcribed list
        // names 5, 12 or 18 differently, so the order only mirrors xEdit.
        foreach (var flag in CommonBits)
        {
            if (flag.Mask == mask)
            {
                return flag.Name;
            }
        }

        if (SignatureBitsFor(game).TryGetValue(signature, out var bits))
        {
            foreach (var flag in bits)
            {
                if (flag.Mask == mask)
                {
                    return flag.Name;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     One entry per set bit of <paramref name="flags" />, lowest bit first: the bit's name from
    ///     <see cref="GetName" />, or <c>bit N (0xMASK)</c> (for example <c>bit 13 (0x00002000)</c>) when
    ///     it has none. Empty when no bit is set.
    /// </summary>
    public static IReadOnlyList<string> DescribeSetBits(BethesdaGame game, string signature, uint flags)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (flags == 0)
        {
            return [];
        }

        var described = new List<string>();
        for (var bit = 0; bit < 32; bit++)
        {
            var mask = 1u << bit;
            if ((flags & mask) == 0)
            {
                continue;
            }

            described.Add(GetName(game, signature, bit) ?? $"bit {bit} (0x{mask:X8})");
        }

        return described;
    }

    private static IReadOnlyDictionary<string, FlagBit[]> SignatureBitsFor(BethesdaGame game)
    {
        return game switch
        {
            BethesdaGame.FalloutNewVegas => FalloutNewVegasBits,
            BethesdaGame.Fallout3 => Fallout3Bits,
            _ => NoSignatureBits
        };
    }

    private static FlagBit Bit(int bit, string name)
    {
        return new FlagBit(1u << bit, name);
    }
}

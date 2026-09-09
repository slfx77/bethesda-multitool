using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     The player character's stat block inside a <c>SAVE.DAT</c> body: 35 big-endian int32 in
///     Fallout's own <c>STAT_</c> order. Located by CONTENT — see <see cref="FalloutSaveBody" />.
/// </summary>
internal sealed class FalloutSaveCharacter
{
    /// <summary>Stat slots the block carries — indices 0-34 of Fallout's <c>STAT_</c> enumeration.</summary>
    public const int StatCount = 35;

    internal FalloutSaveCharacter(int offset, IReadOnlyList<int> stats)
    {
        Offset = offset;
        Stats = stats;
    }

    /// <summary>Byte offset of the block inside the whole save file.</summary>
    public int Offset { get; }

    /// <summary>All 35 stat slots as read, so the ones with no established meaning are not lost.</summary>
    public IReadOnlyList<int> Stats { get; }

    /// <summary>STAT_st.</summary>
    public int Strength => Stats[0];

    /// <summary>STAT_pe.</summary>
    public int Perception => Stats[1];

    /// <summary>STAT_en.</summary>
    public int Endurance => Stats[2];

    /// <summary>STAT_ch.</summary>
    public int Charisma => Stats[3];

    /// <summary>STAT_iq.</summary>
    public int Intelligence => Stats[4];

    /// <summary>STAT_ag.</summary>
    public int Agility => Stats[5];

    /// <summary>STAT_lu.</summary>
    public int Luck => Stats[6];

    /// <summary>Maximum hit points — <c>15 + Strength + 2 x Endurance</c>.</summary>
    public int MaxHitPoints => Stats[7];

    /// <summary>Maximum action points — <c>5 + Agility / 2</c>.</summary>
    public int MaxActionPoints => Stats[8];

    /// <summary>Armour class — Agility.</summary>
    public int ArmorClass => Stats[9];

    /// <summary>Melee damage bonus — <c>max(1, Strength - 5)</c>.</summary>
    public int MeleeDamage => Stats[11];

    /// <summary>Carry weight — <c>25 x Strength + 25</c>.</summary>
    public int CarryWeight => Stats[12];

    /// <summary>Sequence — <c>2 x Perception</c>.</summary>
    public int Sequence => Stats[13];

    /// <summary>Healing rate — <c>max(1, Endurance / 3)</c>.</summary>
    public int HealingRate => Stats[14];

    /// <summary>Critical chance — Luck.</summary>
    public int CriticalChance => Stats[15];

    /// <summary>EMP damage resistance — 100 on a human.</summary>
    public int EmpDamageResistance => Stats[29];

    /// <summary>Radiation resistance — <c>2 x Endurance</c>.</summary>
    public int RadiationResistance => Stats[31];

    /// <summary>Poison resistance — <c>5 x Endurance</c>.</summary>
    public int PoisonResistance => Stats[32];

    /// <summary>The age the player picked at character creation.</summary>
    public int Age => Stats[33];

    /// <summary>0 male, 1 female.</summary>
    public int Gender => Stats[34];

    /// <summary>The seven primary stats, which sum to 40 on a legally created character.</summary>
    public int PrimaryTotal =>
        Strength + Perception + Endurance + Charisma + Intelligence + Agility + Luck;
}

/// <summary>
///     The parts of a Fallout <c>SAVE.DAT</c> BODY that an oracle has actually located. Original
///     RE 2026-09-07 against the retail Fallout 1 SLOT01 (38,339 bytes), cross-checked against the
///     retail Fallout 2 SLOT01 (62,871 bytes). Everything is big-endian, like the header.
///     <para>
///         ⚑ <b>THE GAME-VARIABLE ARRAY, at <see cref="GameVariablesOffset" />.</b> The save
///         stores the campaign globals as a flat int32 array in the order
///         <see cref="FalloutGameVariables" /> reads them out of <c>VAULT13.GAM</c>. The oracle is
///         that <c>.GAM</c>, which lives in <c>MASTER.DAT</c> and not in the save: it declares 618
///         variables (the authors' own <c>// (617)</c> comment on the last one confirms the count)
///         and the Fallout 1 save's 618 int32 from <c>0x7567</c> equal its initial values on
///         <b>618 of 618</b> — including <c>NUM_RADSCORPIONS = 9</c> at index 2,
///         <c>DAYS_TO_VAULT13_DISCOVERY = 180</c> at 9 and <c>VAULT_WATER = 150</c> at 10.
///         A byte-by-byte sweep of the whole file finds that pattern at exactly TWO offsets and
///         nowhere else, so the position is pinned rather than assumed.
///     </para>
///     <para>
///         ⚠⚠ <b>What pins it is EXACTNESS, not a score.</b> 586 of the 618 initials are ZERO, and
///         zero is endian-symmetric, so a near-miss still scores high: reading big-endian one byte
///         early or late scores 566-586 of 618, and reading LITTLE-endian at the correct offset
///         scores 586 of 618. Only requiring all 618 discriminates — the exact 2,472-byte
///         big-endian pattern occurs at exactly <c>0x7567</c> and <c>0x7F23</c> and the
///         little-endian pattern occurs nowhere. ⛔ Do not restate this as "a wrong reading scores
///         near zero"; it does not, and that reasoning would license a much weaker test.
///     </para>
///     <para>
///         ⚑ <b>The array is stored TWICE, byte-identical.</b> ⚠ A start-of-game save cannot show
///         that — every value still equals its initial, so "two copies" and "two different arrays
///         that happen to agree" look the same. The Fallout 2 save is the control drawn from the
///         affected population: it is mid-game, and of the 696 globals its <c>VAULT13.GAM</c>
///         declares (<c>patch000.dat</c>'s copy, which wins the mount) exactly <b>5</b> stored
///         values are NOT the declared initial — <c>GVAR_PLAYER_REPUTATION</c> 15,
///         <c>GVAR_START_ARROYO_TRIAL</c> 1, <c>GVAR_KARMA_WANDERER</c> 1,
///         <c>GVAR_TOWN_REP_ARROYO</c> 65 (initial 50) and <c>GVAR_BUST_SKEEVE</c> 3 — so the block
///         is demonstrably play state rather than a table of initials, and it still occurs twice,
///         at <c>0x7567</c> and again after the map list. So the duplication is structural.
///         ⚠ Which copy the engine reads back is NOT established.
///     </para>
///     <para>
///         ⚑ <b>Between the two copies is the VISITED-MAP LIST</b>: a count byte followed by that
///         many NUL-terminated <c>.SAV</c> names, and they are exactly the sidecars sitting beside
///         the save — <c>V13ENT.SAV</c> on the Fallout 1 slot (1 sidecar) and
///         <c>ARCAVES.SAV</c> / <c>ARTEMPLE.SAV</c> / <c>ARVILLAG.SAV</c> on the Fallout 2 one (3).
///         The names themselves are the oracle; the count merely has to agree with them, and does.
///         Scanning both files for any position satisfying that shape yields exactly one hit each.
///         ⚠⚠ The count's FIELD WIDTH is deliberately not claimed. Read as a u32 it sits at the
///         array's end in the Fallout 2 save but one byte later in the Fallout 1 save, so one of
///         the two games has an unexplained byte there and a single save per game cannot say which.
///         The reader therefore locates the list by its names and takes the count as the single
///         preceding byte, which both files satisfy.
///     </para>
///     <para>
///         ⚑⚑
///         <b>
///             Immediately after the names, a u32 = the UNCOMPRESSED length of
///             <c>AUTOMAP.SAV</c>
///         </b>
///         , and then the second copy of the array begins. Two games, two
///         exact matches against a value from a DIFFERENT FILE: Fallout 1 writes 2,218 at
///         <c>0x7F1F</c> beside a plain 2,218-byte <c>AUTOMAP.SAV</c>, and Fallout 2 writes 10,449
///         at <c>0x8071</c> beside a 4,160-byte gzip that inflates to exactly 10,449.
///         ⚠⚠ This was once published as REFUTED ("2,218 matches but 10,449 does not") because the
///         Fallout 2 automap was measured on disk WITHOUT inflating it — in a track whose own
///         central finding is that Fallout 2 gzips its sidecars. Inflate before you measure.
///     </para>
///     <para>
///         ⚑ <b>THE PLAYER'S STAT BLOCK</b> — 35 int32, located by CONTENT because one save cannot
///         fix an offset. Eleven of Fallout's published derivations are used as the probe, and all
///         eleven hold simultaneously at exactly ONE of the 38,200 candidate byte offsets in the
///         Fallout 1 save: primaries 7/5/6/3/8/5/6 (summing to 40, the character-creation budget),
///         max HP 34 = 15 + 7 + 2x6, max AP 7 = 5 + 5/2, AC 5 = Agility, melee damage 2 =
///         Strength - 5, carry weight 200 = 25 x 7 + 25, sequence 10 = 2 x Perception, healing rate
///         2 = Endurance / 3, critical chance 6 = Luck, EMP resistance 100, radiation resistance
///         12 = 2 x Endurance and poison resistance 30 = 5 x Endurance. Slots 33 and 34 then read
///         as age 29 and gender 0, both in range.
///     </para>
///     <para>
///         ⚠ <b>State the margin honestly.</b> The cheap pre-filter (seven consecutive int32 all in
///         1..10) already cuts 38,200 offsets to <b>2</b> on the Fallout 1 save, so the eleven
///         derivations separate 1 from 2 there, not 1 from 38,200. Where they earn their keep is
///         the Fallout 2 save: 25 offsets pass the pre-filter and <b>none</b> passes the eleven.
///         ⛔ That is also a standing refutation — this probe does NOT find Fallout 2's player, so
///         either that game places the block elsewhere or that character's stats were modified.
///         Fallout 1 only until a second fixture explains it.
///     </para>
///     <para>
///         ⛔ <b>What is NOT located.</b> The 132 bytes between the thumbnail and the variable
///         array — measured, they are 131 NULs followed by ONE non-zero byte at <c>0x7566</c>, 5 on
///         the Fallout 1 save and 1 on the Fallout 2 one, so the "dword at <c>0x7563</c>" once
///         reported is really that single byte; the bytes on either side of the map list; the 35
///         int32 that follow the stat block (all zero here, so nothing distinguishes a bonus array
///         from padding); and the skills, perks, traits, inventory and party.
///     </para>
///     <para>
///         ⛔ The Fallout 1 body then closes with <b>19</b> twenty-byte records from <c>0x8ECC</c>
///         shaped <c>(time, 3, small value, prototype id with family byte 4, 0)</c> — times 264,867
///         / 264,877 / 264,887 / 264,897 / 264,907, i.e. starting six above
///         <see cref="FalloutSaveFile.GameTime" /> and stepping by 10 — after which the stride
///         BREAKS and the remaining ~1,400 bytes are a different shape. ⚠ An earlier reading of
///         "~89 twenty-byte records to EOF" was wrong: 62 of those 89 have a zero first word and
///         three bytes are left over, which is what a mis-assumed stride looks like. All of it is
///         handed back, not guessed at.
///     </para>
/// </summary>
internal static class FalloutSaveBody
{
    /// <summary>
    ///     Where the campaign-global int32 array starts, measured on both retail saves.
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This is an unvalidated constant and the reader does NOT check what it finds
    ///             there.
    ///         </b>
    ///         No plausibility test on a table of campaign globals is oracle-backed —
    ///         any int32 is a legal value — so inventing one would only reject legitimate saves.
    ///         What stands behind the offset is the measurement in the type remarks (the exact
    ///         618-value pattern occurs at this offset and one other in the whole file) and the
    ///         caller's own oracle: hand <see cref="TryReadGameVariables" /> the count from the
    ///         game's <c>.GAM</c> and compare the values against its initials.
    ///     </para>
    /// </summary>
    public const int GameVariablesOffset = 0x7567;

    /// <summary>The seven primary stats a legally created character distributes 40 points over.</summary>
    public const int PrimaryStatCount = 7;

    /// <summary>The character-creation budget the seven primaries sum to.</summary>
    public const int PrimaryStatTotal = 40;

    /// <summary>Most <c>.SAV</c> names a visited-map list is accepted with.</summary>
    private const int MaxVisitedMaps = 64;

    /// <summary>
    ///     Reads the campaign globals. <paramref name="declaredCount" /> must come from the
    ///     game's <c>.GAM</c> — the save does not state it — and the read is refused when the file
    ///     is too short for it.
    /// </summary>
    /// <param name="save">The whole save file.</param>
    /// <param name="declaredCount">Declarations in <c>VAULT13.GAM</c>; 618 for retail Fallout 1.</param>
    /// <param name="values">The globals, in declaration order.</param>
    /// <param name="secondCopyOffset">Where the byte-identical second copy starts, or -1.</param>
    /// <param name="error">Why not, when the read is refused.</param>
    public static bool TryReadGameVariables(
        ReadOnlySpan<byte> save, int declaredCount, out int[] values, out int secondCopyOffset, out string error)
    {
        values = [];
        secondCopyOffset = -1;
        if (declaredCount <= 0)
        {
            error = $"a game-variable count of {declaredCount} is not a count.";
            return false;
        }

        var length = declaredCount * sizeof(int);
        if (save.Length < GameVariablesOffset + length)
        {
            error =
                $"{save.Length} bytes cannot hold {declaredCount} globals at +0x{GameVariablesOffset:X}.";
            return false;
        }

        var block = save.Slice(GameVariablesOffset, length);
        values = new int[declaredCount];
        for (var i = 0; i < declaredCount; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32BigEndian(block[(i * sizeof(int))..]);
        }

        var rest = save[(GameVariablesOffset + length)..];
        var found = rest.IndexOf(block);
        if (found >= 0)
        {
            secondCopyOffset = GameVariablesOffset + length + found;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Finds the visited-map list by its own contents: a count byte, preceded by a zero byte,
    ///     followed by exactly that many NUL-terminated <c>.SAV</c> names. Exactly one position in
    ///     each retail save satisfies it.
    /// </summary>
    public static bool TryReadVisitedMaps(ReadOnlySpan<byte> save, out string[] maps, out int offset)
    {
        maps = [];
        offset = -1;
        for (var at = 2; at < save.Length; at++)
        {
            int count = save[at - 1];
            if (count is < 1 or > MaxVisitedMaps || save[at - 2] != 0)
            {
                continue;
            }

            if (TryReadNames(save, at, count, out var names))
            {
                maps = names;
                offset = at;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Reads the u32 that follows the visited-map list: the UNCOMPRESSED byte length of the
    ///     <c>AUTOMAP.SAV</c> sitting beside the save. See the type remarks — two games, two exact
    ///     matches against a file that is not this one, and ⚠⚠ the Fallout 2 automap must be
    ///     INFLATED before its length is compared (4,160 bytes on disk, 10,449 inflated).
    /// </summary>
    /// <param name="save">The whole save file.</param>
    /// <param name="length">The uncompressed automap length the save states.</param>
    /// <param name="offset">Where that u32 sits, or -1 when the map list was not found.</param>
    public static bool TryReadAutomapLength(ReadOnlySpan<byte> save, out int length, out int offset)
    {
        length = 0;
        offset = -1;
        if (!TryReadVisitedMaps(save, out var maps, out var listOffset))
        {
            return false;
        }

        var after = listOffset;
        foreach (var map in maps)
        {
            after += map.Length + 1;
        }

        if (after + sizeof(uint) > save.Length)
        {
            return false;
        }

        offset = after;
        length = BinaryPrimitives.ReadInt32BigEndian(save[after..]);
        return length > 0;
    }

    /// <summary>
    ///     Finds the player's stat block by requiring every published Fallout derivation to hold
    ///     at once. See the type remarks: eleven constraints, one hit in the retail save.
    /// </summary>
    public static bool TryReadCharacter(ReadOnlySpan<byte> save, out FalloutSaveCharacter character)
    {
        character = null!;
        var stats = new int[FalloutSaveCharacter.StatCount];
        var last = save.Length - FalloutSaveCharacter.StatCount * sizeof(int);
        for (var at = 0; at <= last; at++)
        {
            var window = save[at..];
            var plausible = true;
            for (var i = 0; i < PrimaryStatCount; i++)
            {
                stats[i] = BinaryPrimitives.ReadInt32BigEndian(window[(i * sizeof(int))..]);
                if (stats[i] is < 1 or > 10)
                {
                    plausible = false;
                    break;
                }
            }

            if (!plausible)
            {
                continue;
            }

            for (var i = PrimaryStatCount; i < FalloutSaveCharacter.StatCount; i++)
            {
                stats[i] = BinaryPrimitives.ReadInt32BigEndian(window[(i * sizeof(int))..]);
            }

            if (!DerivationsHold(stats))
            {
                continue;
            }

            character = new FalloutSaveCharacter(at, [.. stats]);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Fallout's published derived-stat formulas, all of which a real stat block satisfies.
    ///     Slot 10 (unarmed damage) is not constrained: it is 0 on the fixture and no formula for
    ///     it is established here.
    /// </summary>
    private static bool DerivationsHold(ReadOnlySpan<int> s)
    {
        var (strength, perception, endurance) = (s[0], s[1], s[2]);
        var (agility, luck) = (s[5], s[6]);

        return s[7] == 15 + strength + 2 * endurance
               && s[8] == 5 + agility / 2
               && s[9] == agility
               && s[11] == Math.Max(1, strength - 5)
               && s[12] == 25 * strength + 25
               && s[13] == 2 * perception
               && s[14] == Math.Max(1, endurance / 3)
               && s[15] == luck
               && s[29] == 100
               && s[31] == 2 * endurance
               && s[32] == 5 * endurance;
    }

    private static bool TryReadNames(ReadOnlySpan<byte> save, int at, int count, out string[] names)
    {
        names = [];
        var read = new string[count];
        var position = at;
        for (var i = 0; i < count; i++)
        {
            var rest = save[position..];
            var end = rest.IndexOf((byte)0);
            if (end is < 5 or > 32)
            {
                return false;
            }

            var field = rest[..end];
            foreach (var b in field)
            {
                if (b is < 0x20 or > 0x7E)
                {
                    return false;
                }
            }

            var name = Encoding.ASCII.GetString(field);
            if (!name.EndsWith(".SAV", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            read[i] = name;
            position += end + 1;
        }

        names = read;
        return true;
    }
}

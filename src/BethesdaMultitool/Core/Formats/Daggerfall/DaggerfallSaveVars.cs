// Scalar offsets, the region-record and faction-record field lists and the emperor's-son name
//   table are ported from daggerfall-unity's SaveVars and FactionFile readers (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/Save/SaveVars.cs and
//   Assets/Scripts/API/FactionFile.cs. License texts are collected centrally in
//   THIRD_PARTY_LICENSES.
//
// Deliberate divergence from the reference (measured on a retail SAVE0, 2026-09-07): the
//   reference DERIVES the faction count from the file length; the file STATES it, in the u32 at
//   +0x17CC (366 there, and (39,768 - 0x17D0) / 92 = 366 exactly). Both are read and the parse is
//   refused when they disagree, so a truncated or padded file cannot be walked as if it were whole.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     One of the 62 per-region blocks: 29 tracked values, 29 flag bytes, 14 more flag bytes, a
///     precipitation override, the severe-punishment flags, the player's legal reputation, the
///     temple whose faithful the region persecutes, and its price adjustment (813 = +/- percent
///     scaled by 10 in region 17 of SAVE0). 29 + 29 + 14 + 1 + 1 + 2 + 2 + 2 = 80 bytes.
/// </summary>
internal sealed class DaggerfallSaveRegion
{
    /// <summary>Bytes in one region block.</summary>
    public const int RecordLength = 80;

    /// <summary>Tracked values and flag bytes per region.</summary>
    public const int ValueCount = 29;

    /// <summary>Second flag run, shorter than the first.</summary>
    public const int Flags2Count = 14;

    private DaggerfallSaveRegion()
    {
    }

    /// <summary>The region's index (0..61), which is also its RegionNames index.</summary>
    public required int Index { get; init; }

    public required ReadOnlyMemory<byte> Values { get; init; }
    public required ReadOnlyMemory<byte> Flags { get; init; }
    public required ReadOnlyMemory<byte> Flags2 { get; init; }
    public required byte PrecipitationOverride { get; init; }
    public required byte SeverePunishmentFlags { get; init; }
    public required short LegalReputation { get; init; }
    public required ushort PersecutedTempleId { get; init; }
    public required ushort PriceAdjustment { get; init; }

    internal static DaggerfallSaveRegion Parse(ReadOnlySpan<byte> bytes, int index)
    {
        if (bytes.Length != RecordLength)
        {
            throw new ArgumentException($"A region block is {RecordLength} bytes; got {bytes.Length}.", nameof(bytes));
        }

        return new DaggerfallSaveRegion
        {
            Index = index,
            Values = bytes[..ValueCount].ToArray(),
            Flags = bytes.Slice(ValueCount, ValueCount).ToArray(),
            Flags2 = bytes.Slice(ValueCount * 2, Flags2Count).ToArray(),
            PrecipitationOverride = bytes[72],
            SeverePunishmentFlags = bytes[73],
            LegalReputation = BinaryPrimitives.ReadInt16LittleEndian(bytes[74..]),
            PersecutedTempleId = BinaryPrimitives.ReadUInt16LittleEndian(bytes[76..]),
            PriceAdjustment = BinaryPrimitives.ReadUInt16LittleEndian(bytes[78..])
        };
    }
}

/// <summary>
///     One 92-byte faction record. The id-to-name pairing is checked against a file OUTSIDE the
///     save: all 366 of SAVE0's records name something <c>ARENA2\FACTION.TXT</c> declares for the
///     same id — see <see cref="DaggerfallSaveVars.ParseFactionText" /> for the two readings that
///     are required to get there (keep EVERY name an id declares, and TrimEnd both sides).
///     <para>
///         Layout: +0 u8 type, +1 i8 region (-1 = none), +2 i8 ruler, +3 char[26] name, +29 i16
///         reputation, +31 i16 power, +33 i16 id, +35 i16 vampire clan, +37 i16 flags, +39 u32
///         ruler name seed, +43 i32 ruler power bonus, +47/+49 i16 flat sprites, +51 i8 face,
///         +52 i8 face2, +53 i8 race, +54 i8 social group, +55 i8 guild group, +56 three i32
///         allies, +68 three i32 enemies, +80/+84/+88 three DOS heap pointers.
///     </para>
///     <para>
///         ⚠ The three trailing words are the game's own heap addresses (15,966,448 and the like on
///         SAVE0), NOT indices into this table — they are surfaced raw and never dereferenced.
///     </para>
/// </summary>
internal sealed class DaggerfallSaveFaction
{
    /// <summary>Bytes in one faction record.</summary>
    public const int RecordLength = 92;

    /// <summary>Bytes reserved for the name.</summary>
    public const int NameLength = 26;

    /// <summary>Ally and enemy slots per faction.</summary>
    public const int RelationSlots = 3;

    private DaggerfallSaveFaction()
    {
    }

    /// <summary>Position in the table.</summary>
    public required int Slot { get; init; }

    public required byte TypeId { get; init; }
    public required sbyte RegionIndex { get; init; }
    public required sbyte RulerType { get; init; }
    public required string Name { get; init; }
    public required short Reputation { get; init; }
    public required short Power { get; init; }
    public required short Id { get; init; }
    public required short VampireClan { get; init; }
    public required short Flags { get; init; }
    public required uint RulerNameSeed { get; init; }
    public required int RulerPowerBonus { get; init; }
    public required short Flat1 { get; init; }
    public required short Flat2 { get; init; }
    public required sbyte Face { get; init; }
    public required sbyte Face2 { get; init; }
    public required sbyte Race { get; init; }
    public required sbyte SocialGroup { get; init; }
    public required sbyte GuildGroup { get; init; }
    public required IReadOnlyList<int> Allies { get; init; }
    public required IReadOnlyList<int> Enemies { get; init; }

    /// <summary>The DOS heap pointers at +80/+84/+88, raw.</summary>
    public required IReadOnlyList<int> HeapPointers { get; init; }

    internal static DaggerfallSaveFaction Parse(ReadOnlySpan<byte> bytes, int slot)
    {
        if (bytes.Length != RecordLength)
        {
            throw new ArgumentException($"A faction record is {RecordLength} bytes; got {bytes.Length}.",
                nameof(bytes));
        }

        var allies = new int[RelationSlots];
        var enemies = new int[RelationSlots];
        for (var i = 0; i < RelationSlots; i++)
        {
            allies[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(56 + i * 4)..]);
            enemies[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(68 + i * 4)..]);
        }

        return new DaggerfallSaveFaction
        {
            Slot = slot,
            TypeId = bytes[0],
            RegionIndex = (sbyte)bytes[1],
            RulerType = (sbyte)bytes[2],
            Name = DaggerfallSaveCharacter.ReadPadded(bytes.Slice(3, NameLength)),
            Reputation = BinaryPrimitives.ReadInt16LittleEndian(bytes[29..]),
            Power = BinaryPrimitives.ReadInt16LittleEndian(bytes[31..]),
            Id = BinaryPrimitives.ReadInt16LittleEndian(bytes[33..]),
            VampireClan = BinaryPrimitives.ReadInt16LittleEndian(bytes[35..]),
            Flags = BinaryPrimitives.ReadInt16LittleEndian(bytes[37..]),
            RulerNameSeed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[39..]),
            RulerPowerBonus = BinaryPrimitives.ReadInt32LittleEndian(bytes[43..]),
            Flat1 = BinaryPrimitives.ReadInt16LittleEndian(bytes[47..]),
            Flat2 = BinaryPrimitives.ReadInt16LittleEndian(bytes[49..]),
            Face = (sbyte)bytes[51],
            Face2 = (sbyte)bytes[52],
            Race = (sbyte)bytes[53],
            SocialGroup = (sbyte)bytes[54],
            GuildGroup = (sbyte)bytes[55],
            Allies = allies,
            Enemies = enemies,
            HeapPointers =
            [
                BinaryPrimitives.ReadInt32LittleEndian(bytes[80..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[84..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[88..])
            ]
        };
    }
}

/// <summary>
///     A Daggerfall save's <c>SAVEVARS.DAT</c>: the game-state scalars, the 62 per-region blocks
///     and the faction table. ALL LITTLE-ENDIAN.
///     <para>
///         ⚑ EXACT TILING on the 39,768-byte retail SAVE0 (2026-09-07): the region blocks run
///         0x3DA + 62 x 80 and end at 0x173A; the u32 at 0x17CC STATES 366 factions and
///         (39,768 - 0x17D0) / 92 is 366 exactly, so the table ends on EOF. Both readings are
///         required to agree — a file whose stated count does not reach EOF is refused.
///     </para>
///     <para>
///         ⚑ Oracles from OUTSIDE the file: 366 of the 366 factions match <c>FACTION.TXT</c>'s
///         id-to-name pairing (measured 2026-09-07 — the 366 records span 365 distinct ids because
///         id 77 appears TWICE, once per name FACTION.TXT declares for it);
///         <see cref="GameTimeMinutes" /> 524,043 is 22:03 on 4 Morning Star 3E405 and
///         <see cref="IsDay" /> is 0 at that hour; <see cref="CurrentWorldRecordId" /> 0xC3810001
///         is (Privateer's Hold's exterior LocationId 50049 &lt;&lt; 16) | 1 and is the parent the
///         Move-record copy at 0x2F8 names; <see cref="CurrentRegionIndex" /> 17 is the region
///         MAPS.BSA files Privateer's Hold under.
///     </para>
///     <para>
///         ⚠ Unknowns are surfaced raw rather than named: the eight bytes at 0x1748 and the u32 at
///         0x1796 (525,007 — a game time in the future).
///     </para>
///     <para>
///         ⛔ 0x1738 is NOT an unknown scalar and must not be surfaced as one — it is the LAST
///         FIELD OF THE LAST REGION BLOCK: 0x3DA + 61 x 80 + 78 = 0x1738 is region 61's
///         <see cref="DaggerfallSaveRegion.PriceAdjustment" /> (886 on SAVE0), and
///         <see cref="CurrentRegionIndex" /> at 0x173A is the first byte past the table. An
///         earlier pass exposed the same two bytes twice and published them as open. The base is
///         MEASURED, not assumed: read at 0x3DA the 62 price adjustments are 757..1246 with region
///         17 = 813; read at 0x3D2 — the base the old "end at 0x1732" sentence implied — all 62
///         price adjustments, legal reputations and persecuted-temple ids are 0.
///     </para>
/// </summary>
internal sealed class DaggerfallSaveVars
{
    /// <summary>The only file this parser reads.</summary>
    public const string FileName = "SAVEVARS.DAT";

    /// <summary>Five i32 biography modifiers.</summary>
    public const int BiographyModifiersOffset = 0x30;

    /// <summary>How many biography modifiers there are.</summary>
    public const int BiographyModifierCount = 5;

    /// <summary>u8 index into <see cref="EmperorSonNames" />.</summary>
    public const int EmperorSonOffset = 0x7C;

    /// <summary>i16 travel-option bitfield.</summary>
    public const int TravelFlagsOffset = 0xF5;

    /// <summary>A byte-for-byte copy of the tree's Move (type 4) record ROOT.</summary>
    public const int PlayerPositionRootOffset = 0x2F8;

    /// <summary>Four words of Mace of Molag Bal state.</summary>
    public const int MaceOfMolagBalOffset = 0x33F;

    /// <summary>64 bytes of global quest variables.</summary>
    public const int GlobalQuestVarsOffset = 0x34F;

    /// <summary>Length of the global quest-variable block.</summary>
    public const int GlobalQuestVarsLength = 64;

    /// <summary>The game clock, in MINUTES.</summary>
    public const int GameTimeOffset = 0x3C9;

    /// <summary>
    ///     First region block; 62 of them follow, so the table ends at 0x173A — where
    ///     <see cref="DaggerfallSaveVars.CurrentRegionIndex" /> begins. Region 61's trailing
    ///     PriceAdjustment therefore occupies 0x1738..0x1739.
    /// </summary>
    public const int RegionsOffset = 0x3DA;

    /// <summary>Regions in the game.</summary>
    public const int RegionCount = 62;

    /// <summary>u32 stating how many faction records follow at <see cref="FactionTableOffset" />.</summary>
    public const int FactionCountOffset = 0x17CC;

    /// <summary>First faction record; the table runs to EOF.</summary>
    public const int FactionTableOffset = 0x17D0;

    /// <summary>Six bytes of per-climate weather, duplicated at 0x17A2.</summary>
    public const int ClimateWeatherCount = 6;

    /// <summary>Bit set in <see cref="UiFlags" /> while the weapon is drawn.</summary>
    public const byte UiFlagWeaponDrawn = 0x40;

    /// <summary>The six names the emperor's son may take, in index order.</summary>
    public static readonly IReadOnlyList<string> EmperorSonNames =
        ["Pelagius", "Cephorus", "Uriel", "Cassynder", "Voragiel", "Trabbatus"];

    private DaggerfallSaveVars()
    {
    }

    /// <summary>Source file name, for messages.</summary>
    public required string Name { get; init; }

    /// <summary>Physical file length; the faction table ended exactly here.</summary>
    public required int Length { get; init; }

    /// <summary>Resist disease, resist magic, avoid hit, resist poison, fatigue (0, -5, 0, 0, 0 on SAVE0).</summary>
    public required IReadOnlyList<int> BiographyModifiers { get; init; }

    /// <summary>Index into <see cref="EmperorSonNames" /> (1 = Cephorus on SAVE0).</summary>
    public required byte EmperorSonIndex { get; init; }

    /// <summary>The emperor's son's name, or the raw index when it is outside the table.</summary>
    public string EmperorSonName =>
        EmperorSonIndex < EmperorSonNames.Count ? EmperorSonNames[EmperorSonIndex] : $"son {EmperorSonIndex}";

    /// <summary>1 cautiously, 2 recklessly, 4 foot/horse, 8 ship, 0x10 inns, 0x20 camp out.</summary>
    public required short TravelFlags { get; init; }

    /// <summary>The 71-byte Move-record copy at 0x2F8, decoded with the tree's root reader.</summary>
    public required DaggerfallSaveTreeRecord PlayerPositionRoot { get; init; }

    /// <summary>Four words of Mace of Molag Bal state (all zero on SAVE0).</summary>
    public required IReadOnlyList<int> MaceOfMolagBal { get; init; }

    /// <summary>64 raw bytes of global quest variables.</summary>
    public required ReadOnlyMemory<byte> GlobalQuestVars { get; init; }

    /// <summary>Magicka the last cast spell cost.</summary>
    public required short LastSpellCost { get; init; }

    /// <summary>Non-zero during daylight; 0 on SAVE0, whose clock reads 22:03.</summary>
    public required byte IsDay { get; init; }

    public required byte CrimeCommitted { get; init; }
    public required byte InDungeonWater { get; init; }
    public required int BreathRemaining { get; init; }

    /// <summary>Six per-climate weather bytes.</summary>
    public required ReadOnlyMemory<byte> ClimateWeathers { get; init; }

    /// <summary>The copy at 0x17A2, which the game reads; identical to <see cref="ClimateWeathers" /> on SAVE0.</summary>
    public required ReadOnlyMemory<byte> ClimateWeathers2 { get; init; }

    /// <summary>UI state; <see cref="UiFlagWeaponDrawn" /> is the only bit measured.</summary>
    public required byte UiFlags { get; init; }

    /// <summary>True when <see cref="UiFlagWeaponDrawn" /> is set.</summary>
    public bool IsWeaponDrawn => (UiFlags & UiFlagWeaponDrawn) != 0;

    /// <summary>The game clock in minutes (524,043 on SAVE0).</summary>
    public required uint GameTimeMinutes { get; init; }

    /// <summary>The clock as a calendar date.</summary>
    public DaggerfallGameTime GameTime => DaggerfallGameTime.FromMinutes(GameTimeMinutes);

    public required byte UsingLeftHandWeapon { get; init; }

    /// <summary>
    ///     The 62 region blocks, in region-index order. The last one ENDS the table at 0x173A;
    ///     <c>Regions[61].PriceAdjustment</c> is the u16 at 0x1738 (886 on SAVE0).
    /// </summary>
    public required IReadOnlyList<DaggerfallSaveRegion> Regions { get; init; }

    /// <summary>Region the player is in (17 = Daggerfall on SAVE0).</summary>
    public required byte CurrentRegionIndex { get; init; }

    /// <summary>8 map reveal, 0x20 no collision, 0x40 god mode, 0x80 enemies cast no spells.</summary>
    public required byte CheatFlags { get; init; }

    /// <summary>The eight bytes at 0x1748 (7F FF FF D8 D6 00 00 00 on SAVE0); meaning open.</summary>
    public required ReadOnlyMemory<byte> Unknown1748 { get; init; }

    /// <summary>0 none, 25,600,000 small, 51,200,000 large.</summary>
    public required int Ship { get; init; }

    /// <summary>The u32 at 0x1796 (525,007 on SAVE0 — a game time ~16 hours ahead); meaning open.</summary>
    public required uint Unknown1796 { get; init; }

    /// <summary>Game time of the last skill check (524,036 on SAVE0, seven minutes before the save).</summary>
    public required uint LastSkillCheckTime { get; init; }

    /// <summary>Dungeon water level (10,000 on SAVE0).</summary>
    public required uint DungeonWaterLevel { get; init; }

    /// <summary>The World record id of the current location: (exterior LocationId &lt;&lt; 16) | 1.</summary>
    public required uint CurrentWorldRecordId { get; init; }

    /// <summary>The exterior LocationId the current location is filed under (50,049 on SAVE0).</summary>
    public ushort CurrentLocationId => (ushort)(CurrentWorldRecordId >> 16);

    /// <summary>The count the FILE states at 0x17CC.</summary>
    public required int DeclaredFactionCount { get; init; }

    /// <summary>The faction table, <see cref="DeclaredFactionCount" /> records long.</summary>
    public required IReadOnlyList<DaggerfallSaveFaction> Factions { get; init; }

    /// <summary>The first faction with a given id, or null.</summary>
    public DaggerfallSaveFaction? FindFaction(int id)
    {
        return Factions.FirstOrDefault(f => f.Id == id);
    }

    /// <summary>Content probe: long enough to hold a stated faction table that reaches EOF exactly.</summary>
    public static bool IsSaveVars(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < FactionTableOffset)
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes[FactionCountOffset..]);
        return declared <= int.MaxValue / DaggerfallSaveFaction.RecordLength
               && (long)declared * DaggerfallSaveFaction.RecordLength == bytes.Length - FactionTableOffset;
    }

    /// <summary>Loads and parses a file.</summary>
    public static DaggerfallSaveVars Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>Parses a complete image; throws <see cref="InvalidDataException" /> unless it tiles.</summary>
    public static DaggerfallSaveVars Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < FactionTableOffset)
        {
            throw new InvalidDataException(
                $"{name}: {bytes.Length} bytes stops short of the faction table at 0x{FactionTableOffset:X}.");
        }

        var span = bytes.AsSpan();
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(span[FactionCountOffset..]);
        var tableBytes = bytes.Length - FactionTableOffset;
        if (declared > int.MaxValue / DaggerfallSaveFaction.RecordLength
            || (long)declared * DaggerfallSaveFaction.RecordLength != tableBytes)
        {
            throw new InvalidDataException(
                $"{name}: the file states {declared} factions at 0x{FactionCountOffset:X}, which is "
                + $"{(long)declared * DaggerfallSaveFaction.RecordLength} bytes, but {tableBytes} follow "
                + $"0x{FactionTableOffset:X}.");
        }

        var biography = new int[BiographyModifierCount];
        for (var i = 0; i < BiographyModifierCount; i++)
        {
            biography[i] = BinaryPrimitives.ReadInt32LittleEndian(span[(BiographyModifiersOffset + i * 4)..]);
        }

        var mace = new int[4];
        for (var i = 0; i < 4; i++)
        {
            mace[i] = BinaryPrimitives.ReadInt32LittleEndian(span[(MaceOfMolagBalOffset + i * 4)..]);
        }

        var regions = new DaggerfallSaveRegion[RegionCount];
        for (var i = 0; i < RegionCount; i++)
        {
            regions[i] = DaggerfallSaveRegion.Parse(
                span.Slice(RegionsOffset + i * DaggerfallSaveRegion.RecordLength, DaggerfallSaveRegion.RecordLength),
                i);
        }

        var factions = new DaggerfallSaveFaction[(int)declared];
        for (var i = 0; i < factions.Length; i++)
        {
            factions[i] = DaggerfallSaveFaction.Parse(
                span.Slice(FactionTableOffset + i * DaggerfallSaveFaction.RecordLength,
                    DaggerfallSaveFaction.RecordLength), i);
        }

        var memory = new ReadOnlyMemory<byte>(bytes);
        return new DaggerfallSaveVars
        {
            Name = name,
            Length = bytes.Length,
            BiographyModifiers = biography,
            EmperorSonIndex = span[EmperorSonOffset],
            TravelFlags = BinaryPrimitives.ReadInt16LittleEndian(span[TravelFlagsOffset..]),
            PlayerPositionRoot = new DaggerfallSaveTreeRecord(
                PlayerPositionRootOffset,
                DaggerfallSaveTree.RecordRootLength,
                memory.Slice(PlayerPositionRootOffset, DaggerfallSaveTree.RecordRootLength)),
            MaceOfMolagBal = mace,
            GlobalQuestVars = memory.Slice(GlobalQuestVarsOffset, GlobalQuestVarsLength),
            LastSpellCost = BinaryPrimitives.ReadInt16LittleEndian(span[0x38F..]),
            IsDay = span[0x391],
            CrimeCommitted = span[0x3A3],
            InDungeonWater = span[0x3A6],
            BreathRemaining = BinaryPrimitives.ReadInt32LittleEndian(span[0x3AB..]),
            ClimateWeathers = memory.Slice(0x3B7, ClimateWeatherCount),
            ClimateWeathers2 = memory.Slice(0x17A2, ClimateWeatherCount),
            UiFlags = span[0x3BF],
            GameTimeMinutes = BinaryPrimitives.ReadUInt32LittleEndian(span[GameTimeOffset..]),
            UsingLeftHandWeapon = span[0x3D9],
            Regions = regions,
            // 0x173A is the first byte AFTER the region table (0x3DA + 62 x 80), not a gap: the
            // two bytes at 0x1738 are region 61's PriceAdjustment and are already in Regions[61].
            CurrentRegionIndex = span[0x173A],
            CheatFlags = span[0x173B],
            Unknown1748 = memory.Slice(0x1748, 8),
            Ship = BinaryPrimitives.ReadInt32LittleEndian(span[0x1750..]),
            Unknown1796 = BinaryPrimitives.ReadUInt32LittleEndian(span[0x1796..]),
            LastSkillCheckTime = BinaryPrimitives.ReadUInt32LittleEndian(span[0x179A..]),
            DungeonWaterLevel = BinaryPrimitives.ReadUInt32LittleEndian(span[0x17A8..]),
            CurrentWorldRecordId = BinaryPrimitives.ReadUInt32LittleEndian(span[0x17AC..]),
            DeclaredFactionCount = (int)declared,
            Factions = factions
        };
    }

    /// <summary>
    ///     Parses <c>ARENA2\FACTION.TXT</c> into id -&gt; EVERY name declared for that id — the
    ///     oracle this table is checked against.
    ///     <para>
    ///         ⚠ An id may carry more than one name and all of them are kept: retail declares 77
    ///         twice ("The Guildmaster" and "The Master of Initiates") and the save carries BOTH,
    ///         at table slots 129 and 135, so a first-name-wins map reports a false mismatch on the
    ///         second. With every name kept, and trailing spaces trimmed on both sides (save
    ///         faction 420 is "The Dust Witches " with a space the game never trimmed), SAVE0's
    ///         table matches 366 of 366 — measured 2026-09-07 over all three readings: all-names +
    ///         TrimEnd = 0 mismatches, first-name-only = 1 (id 77), no trim = 1 (id 420). An
    ///         earlier pass shipped the first-name-only reading and blamed the data for the miss.
    ///     </para>
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> ParseFactionText(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var names = new Dictionary<int, List<string>>();
        var current = -1;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#'))
            {
                var digits = line[1..];
                var end = 0;
                while (end < digits.Length && char.IsAsciiDigit(digits[end]))
                {
                    end++;
                }

                current = end > 0 && int.TryParse(digits[..end], out var id) ? id : -1;
            }
            else if (current >= 0 && line.StartsWith("name:", StringComparison.Ordinal))
            {
                if (!names.TryGetValue(current, out var list))
                {
                    list = [];
                    names[current] = list;
                }

                list.Add(line[5..].Trim());
            }
        }

        return names.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }
}

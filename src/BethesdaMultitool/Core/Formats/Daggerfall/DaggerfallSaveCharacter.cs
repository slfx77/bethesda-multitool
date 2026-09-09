// Field layout ported from daggerfall-unity's CharacterRecord and ClassFile (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/Save/CharacterRecord.cs and
//   Assets/Scripts/API/ClassFile.cs. License texts are collected centrally in THIRD_PARTY_LICENSES.
//
// Deliberate divergences from the reference (measured on a retail SAVE0, 2026-09-07):
//   * The u32 at +0x1FD (the reference's "lastTimeVampireNeedToKillSatiated") equals SAVEVARS
//     gameTime on a level-1 non-vampire, and the u32 at +0x20D — which the reference skips —
//     holds gameTime - 1. Both are surfaced as plain time stamps under neutral names.
//   * The u32 at +0x1F9 (51,250 on SAVE0; the reference reads nothing there) is surfaced raw.
//
// ⚠ These three offsets were RE-MEASURED 2026-09-07 (second pass, independent Python read of the
//   same fixture) because the survey note that drove this file placed them one byte lower, at
//   +0x1F8/+0x1FC/+0x209, and blamed the reference for an off-by-one. The bytes settle it: the
//   body runs "... 00 00 00 00 00 00 00 | 32 C8 00 00 | 0B FF 07 00" with the first of those
//   seven zeros at +0x1F2, so 0x32 sits at +0x1F9 and 0x0B at +0x1FD. Read here: 51,250 and
//   524,043 (== SAVEVARS gameTime) and 524,042 at +0x20D. Read one byte lower: 13,120,000,
//   134,155,008 and 0 — arithmetic no oracle supports. The survey's off-by-one is the error;
//   the reference's offsets are right and are kept.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     The 635-byte body of a Character (type 0x03) record — the player. ALL LITTLE-ENDIAN.
///     <para>
///         Layout: +0 char[32] name; +32 8 x i16 current STR INT WIL AGI END PER SPD LUC; +48 the
///         same eight base values; +64 u8 gender (bit 0 = female); +65 u8 transport flags (1 foot,
///         2 horse, 4 cart); +66 u8 min metal to hit; +67 u8 race (+1 = the reference's race id:
///         2 = Nord); +68 7 x i8 armour values; +0x50/+0x54 u32 skills-raised flags; +0x58 i32
///         starting level-up skill sum; +0x5C i16 base health; +0x74/+0x78 u32 house/ship;
///         +0x7C/+0x7E i16 current/max health; +0x80 u8 face; +0x81 u8 level; +0x83 u8 reflexes;
///         +0x85 u32 gold; +0x89 4 x u8 magic-effect flags; +0x8D/+0x8F i16 spell points current/
///         max; +0x91.. five i16 reputations (commoners, merchants, scholars, nobility,
///         underworld); +0x9B u16 fatigue in 1/64 units; +0x9D 35 x {i16 value, i16 use counter,
///         i16 zero} in skill-id order; +0x16F 27 x u32 equipped-item record ids; +0x1F2 u8
///         original race; +0x1F9 u32 (open); +0x1FD u32 game-time stamp; +0x20D u32 game-time
///         stamp; +0x230 the 74-byte CAREER (a CLASS??.CFG image, <see cref="DaggerfallCareer" />);
///         +0x27A one byte.
///     </para>
///     <para>
///         ⚑ Oracles on SAVE0: the name equals SAVENAME.TXT ("Hans"); the career block is
///         BYTE-IDENTICAL to CLASS12.CFG ("Monk") and no other CLASS file matches more than 47/74;
///         starting level-up skill sum 162 = the three primaries (33+31+30) + the top two majors
///         (25+21) + the top minor (22) computed from THIS skill array through THIS career's slot
///         ids, which pins both the 6-byte skill stride and the career slot layout; fatigue 7,491
///         ≤ (STR+END) x 64 = 7,744; both equipped ids resolve to Item records; the stamp at +0x1FD
///         equals SAVEVARS gameTime 524,043.
///     </para>
/// </summary>
internal sealed class DaggerfallSaveCharacter
{
    /// <summary>Bytes in the body.</summary>
    public const int DataLength = 635;

    private const int SkillCount = 35;
    private const int EquippedSlots = 27;

    /// <summary>Attribute count and their order.</summary>
    public static readonly IReadOnlyList<string> AttributeNames =
        ["Strength", "Intelligence", "Willpower", "Agility", "Endurance", "Personality", "Speed", "Luck"];

    /// <summary>The 35 skills in id order (the order the record stores them).</summary>
    public static readonly IReadOnlyList<string> SkillNames =
    [
        "Medical", "Etiquette", "Streetwise", "Jumping", "Orcish", "Harpy", "Giantish", "Dragonish",
        "Nymph", "Daedric", "Spriggan", "Centaurian", "Impish", "Lockpicking", "Mercantile",
        "Pickpocket", "Stealth", "Swimming", "Climbing", "Backstabbing", "Dodging", "Running",
        "Destruction", "Restoration", "Illusion", "Alteration", "Thaumaturgy", "Mysticism",
        "Short Blade", "Long Blade", "Hand-to-Hand", "Axe", "Blunt Weapon", "Archery", "Critical Strike"
    ];

    /// <summary>Race names indexed by the RAW race byte (the reference's id is one higher).</summary>
    public static readonly IReadOnlyList<string> RaceNames =
    [
        "Breton", "Redguard", "Nord", "Dark Elf", "High Elf", "Wood Elf", "Khajiit", "Argonian", "Vampire", "Werewolf",
        "Wereboar"
    ];

    /// <summary>Reflex names by the raw byte, from the reference's PlayerReflexes.</summary>
    public static readonly IReadOnlyList<string> ReflexNames = ["Very High", "High", "Average", "Low", "Very Low"];

    private DaggerfallSaveCharacter()
    {
    }

    public required string Name { get; init; }
    public required IReadOnlyList<short> CurrentAttributes { get; init; }
    public required IReadOnlyList<short> BaseAttributes { get; init; }
    public required byte GenderByte { get; init; }
    public bool IsFemale => (GenderByte & 1) == 1;
    public required byte TransportFlags { get; init; }
    public required byte MinMetalToHit { get; init; }
    public required byte RaceByte { get; init; }

    /// <summary>The race name, or the raw byte when it is outside the table.</summary>
    public string RaceName => RaceByte < RaceNames.Count ? RaceNames[RaceByte] : $"race {RaceByte}";

    public required IReadOnlyList<sbyte> ArmorValues { get; init; }
    public required uint SkillsRaisedThisLevel1 { get; init; }
    public required uint SkillsRaisedThisLevel2 { get; init; }
    public required int StartingLevelUpSkillSum { get; init; }
    public required short BaseHealth { get; init; }
    public required uint PlayerHouse { get; init; }
    public required uint PlayerShip { get; init; }
    public required short CurrentHealth { get; init; }
    public required short MaxHealth { get; init; }
    public required byte FaceIndex { get; init; }
    public required byte Level { get; init; }
    public required byte ReflexesByte { get; init; }

    public string ReflexesName =>
        ReflexesByte < ReflexNames.Count ? ReflexNames[ReflexesByte] : $"reflexes {ReflexesByte}";

    public required uint Gold { get; init; }
    public required IReadOnlyList<byte> MagicEffectFlags { get; init; }
    public required short SpellPoints { get; init; }
    public required short MaxSpellPoints { get; init; }
    public required short ReputationCommoners { get; init; }
    public required short ReputationMerchants { get; init; }
    public required short ReputationScholars { get; init; }
    public required short ReputationNobility { get; init; }
    public required short ReputationUnderworld { get; init; }

    /// <summary>Fatigue in 1/64 units; the maximum is (STR + END) x 64.</summary>
    public required ushort Fatigue { get; init; }

    /// <summary>The 35 skills in id order: value and use counter.</summary>
    public required IReadOnlyList<DaggerfallSaveSkill> Skills { get; init; }

    /// <summary>The 27 equipped-item slots (record ids, 0 when empty).</summary>
    public required IReadOnlyList<uint> EquippedItemIds { get; init; }

    /// <summary>The u8 at +0x1F2 — the original race while transformed.</summary>
    public required byte OriginalRaceByte { get; init; }

    /// <summary>The u32 at +0x1F9 (51,250 on SAVE0; meaning open).</summary>
    public required uint Unknown1F9 { get; init; }

    /// <summary>The u32 at +0x1FD — equal to SAVEVARS gameTime on SAVE0.</summary>
    public required uint TimeStamp1FD { get; init; }

    /// <summary>The u32 at +0x20D — gameTime - 1 on SAVE0; the reference skips these bytes.</summary>
    public required uint TimeStamp20D { get; init; }

    /// <summary>The 74-byte career block at +0x230.</summary>
    public required DaggerfallCareer Career { get; init; }

    /// <summary>A one-line description: "Hans, level 1 Nord Monk (male), health 22/39, ...".</summary>
    public string Describe()
    {
        return $"{Name}, level {Level} {RaceName} {Career.Name} ({(IsFemale ? "female" : "male")}), "
               + $"health {CurrentHealth}/{MaxHealth}, spell points {SpellPoints}/{MaxSpellPoints}, "
               + $"{Gold} gold, {ReflexesName.ToLowerInvariant()} reflexes";
    }

    /// <summary>Parses the body (the bytes after the 71-byte root); at least 635 are needed.</summary>
    public static DaggerfallSaveCharacter Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < DataLength)
        {
            throw new InvalidDataException($"A character body is {DataLength} bytes; got {data.Length}.");
        }

        var current = new short[8];
        var baseValues = new short[8];
        for (var i = 0; i < 8; i++)
        {
            current[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(32 + i * 2)..]);
            baseValues[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(48 + i * 2)..]);
        }

        var armor = new sbyte[7];
        for (var i = 0; i < 7; i++)
        {
            armor[i] = (sbyte)data[68 + i];
        }

        var skills = new DaggerfallSaveSkill[SkillCount];
        for (var i = 0; i < SkillCount; i++)
        {
            var at = 0x9D + i * 6;
            skills[i] = new DaggerfallSaveSkill(
                i,
                BinaryPrimitives.ReadInt16LittleEndian(data[at..]),
                BinaryPrimitives.ReadInt16LittleEndian(data[(at + 2)..]),
                BinaryPrimitives.ReadInt16LittleEndian(data[(at + 4)..]));
        }

        var equipped = new uint[EquippedSlots];
        for (var i = 0; i < EquippedSlots; i++)
        {
            equipped[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(0x16F + i * 4)..]);
        }

        return new DaggerfallSaveCharacter
        {
            Name = ReadPadded(data[..32]),
            CurrentAttributes = current,
            BaseAttributes = baseValues,
            GenderByte = data[64],
            TransportFlags = data[65],
            MinMetalToHit = data[66],
            RaceByte = data[67],
            ArmorValues = armor,
            SkillsRaisedThisLevel1 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x50..]),
            SkillsRaisedThisLevel2 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x54..]),
            StartingLevelUpSkillSum = BinaryPrimitives.ReadInt32LittleEndian(data[0x58..]),
            BaseHealth = BinaryPrimitives.ReadInt16LittleEndian(data[0x5C..]),
            PlayerHouse = BinaryPrimitives.ReadUInt32LittleEndian(data[0x74..]),
            PlayerShip = BinaryPrimitives.ReadUInt32LittleEndian(data[0x78..]),
            CurrentHealth = BinaryPrimitives.ReadInt16LittleEndian(data[0x7C..]),
            MaxHealth = BinaryPrimitives.ReadInt16LittleEndian(data[0x7E..]),
            FaceIndex = data[0x80],
            Level = data[0x81],
            ReflexesByte = data[0x83],
            Gold = BinaryPrimitives.ReadUInt32LittleEndian(data[0x85..]),
            MagicEffectFlags = data.Slice(0x89, 4).ToArray(),
            SpellPoints = BinaryPrimitives.ReadInt16LittleEndian(data[0x8D..]),
            MaxSpellPoints = BinaryPrimitives.ReadInt16LittleEndian(data[0x8F..]),
            ReputationCommoners = BinaryPrimitives.ReadInt16LittleEndian(data[0x91..]),
            ReputationMerchants = BinaryPrimitives.ReadInt16LittleEndian(data[0x93..]),
            ReputationScholars = BinaryPrimitives.ReadInt16LittleEndian(data[0x95..]),
            ReputationNobility = BinaryPrimitives.ReadInt16LittleEndian(data[0x97..]),
            ReputationUnderworld = BinaryPrimitives.ReadInt16LittleEndian(data[0x99..]),
            Fatigue = BinaryPrimitives.ReadUInt16LittleEndian(data[0x9B..]),
            Skills = skills,
            EquippedItemIds = equipped,
            OriginalRaceByte = data[0x1F2],
            Unknown1F9 = BinaryPrimitives.ReadUInt32LittleEndian(data[0x1F9..]),
            TimeStamp1FD = BinaryPrimitives.ReadUInt32LittleEndian(data[0x1FD..]),
            TimeStamp20D = BinaryPrimitives.ReadUInt32LittleEndian(data[0x20D..]),
            Career = DaggerfallCareer.Parse(data.Slice(0x230, DaggerfallCareer.Length))
        };
    }

    /// <summary>A fixed-width NUL-padded field (the name is padded, not terminated).</summary>
    internal static string ReadPadded(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}

/// <summary>One skill slot: the id, its value, its use counter, and the third word (always zero on SAVE0).</summary>
internal readonly record struct DaggerfallSaveSkill(int Id, short Value, short UseCounter, short Unused)
{
    public string Name => Id < DaggerfallSaveCharacter.SkillNames.Count
        ? DaggerfallSaveCharacter.SkillNames[Id]
        : $"skill {Id}";
}

/// <summary>
///     A 74-byte career block — the format of a <c>CLASS??.CFG</c> file, embedded whole in the
///     character record (SAVE0's is byte-identical to CLASS12.CFG, "Monk"). Layout, in the
///     reference's read order: +0 u8 resistance flags, +1 immunity, +2 low tolerance, +3 critical
///     weakness, +4 u16 ability/spell-point bitfield, +6 u8 rapid healing, +7 u8 regeneration,
///     +8 u8, +9 u8 spell absorption, +10 u8 attack modifier, +11 u16 forbidden materials,
///     +13 3 bytes weapon/armour/shield bitfield, +16 3 x u8 primary skill ids, +19 3 x u8 major,
///     +22 6 x u8 minor, +28 char[16] name, +44 8 bytes, +52 u16 hit points per level, +54 u32
///     advancement multiplier, +58 8 x u16 attributes. Only the skill slots, the name, the hit
///     points and the attributes are decoded; the leading flags are kept raw.
///     <para>
///         Retail CLASS files: 00 Mage, 01 Spellsword, 02 Battlemage, 03 Sorcerer, 04 Healer,
///         05 Nightblade, 06 Bard, 07 Burglar, 08 Rogue, 09 Acrobat, 10 Thief, 11 Assassin,
///         12 Monk, 13 Archer, 14 Ranger, 15 Barbarian, 16 Warrior, 17/18 Knight.
///     </para>
/// </summary>
internal sealed class DaggerfallCareer
{
    /// <summary>Bytes in the block (and in a CLASS??.CFG file).</summary>
    public const int Length = 74;

    private DaggerfallCareer()
    {
    }

    /// <summary>The whole 74 bytes, for byte-level comparison with a CLASS file.</summary>
    public required ReadOnlyMemory<byte> Raw { get; init; }

    /// <summary>The 16 flag bytes before the skill slots, undecoded.</summary>
    public ReadOnlyMemory<byte> Flags => Raw[..16];

    public required IReadOnlyList<byte> PrimarySkillIds { get; init; }
    public required IReadOnlyList<byte> MajorSkillIds { get; init; }
    public required IReadOnlyList<byte> MinorSkillIds { get; init; }
    public required string Name { get; init; }
    public required ushort HitPointsPerLevel { get; init; }
    public required uint AdvancementMultiplier { get; init; }
    public required IReadOnlyList<ushort> Attributes { get; init; }

    public static DaggerfallCareer Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"A career block is {Length} bytes; got {bytes.Length}.", nameof(bytes));
        }

        var attributes = new ushort[8];
        for (var i = 0; i < 8; i++)
        {
            attributes[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(58 + i * 2)..]);
        }

        return new DaggerfallCareer
        {
            Raw = bytes.ToArray(),
            PrimarySkillIds = bytes.Slice(16, 3).ToArray(),
            MajorSkillIds = bytes.Slice(19, 3).ToArray(),
            MinorSkillIds = bytes.Slice(22, 6).ToArray(),
            Name = DaggerfallSaveCharacter.ReadPadded(bytes.Slice(28, 16)),
            HitPointsPerLevel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[52..]),
            AdvancementMultiplier = BinaryPrimitives.ReadUInt32LittleEndian(bytes[54..]),
            Attributes = attributes
        };
    }
}

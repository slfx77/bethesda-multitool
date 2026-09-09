using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>One of the 21 skills: the current value, how often it has been used, and the base value.</summary>
internal readonly record struct BattlespireSaveSkill(uint Value, uint UseCount, uint Base);

/// <summary>
///     The 20-byte advantage/disadvantage block a character carries twice (current at +467, base
///     at +523). The Resistance/Immunity/LowTolerance/CriticalWeakness bytes are element bit
///     arrays, ForbiddenWeapons a weapon bit array, ForbiddenArmor and ForbiddenMaterial the
///     MAXIMUM class allowed (UESP's reading), IncreaseMagery a fixed-point multiplier (256 = 1x).
/// </summary>
internal readonly record struct BattlespireSaveAdvantages(
    byte Resistance,
    byte Immunity,
    bool AcuteHearing,
    bool SpellAbsorption,
    bool RapidHealing,
    bool RegenerateHealth,
    bool RegenerateSpellPoints,
    bool Athleticism,
    uint IncreaseMagery,
    bool AdrenalineRush,
    byte LowTolerance,
    byte CriticalWeakness,
    bool NoRegenerateSpellPoints,
    byte ForbiddenWeapons,
    byte ForbiddenArmor,
    bool ForbiddenShield,
    byte ForbiddenMaterial)
{
    /// <summary>Bytes in the block.</summary>
    public const int Length = 20;

    /// <summary>Reads the block from its first byte.</summary>
    public static BattlespireSaveAdvantages Read(ReadOnlySpan<byte> bytes)
    {
        return new BattlespireSaveAdvantages(
            bytes[0], bytes[1], bytes[2] != 0, bytes[3] != 0, bytes[4] != 0, bytes[5] != 0, bytes[6] != 0,
            bytes[7] != 0, BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), bytes[12] != 0, bytes[13],
            bytes[14], bytes[15] != 0, bytes[16], bytes[17], bytes[18] != 0, bytes[19]);
    }
}

/// <summary>
///     Which record a character body came from. It matters because the tail of the body is NOT
///     shared: <c>+803</c> is a monster field (see <see cref="BattlespireSaveCharacter.SaveVarId" />).
/// </summary>
internal enum BattlespireSaveCharacterKind
{
    /// <summary>A body whose record does not say which it is — a type-0 tombstone of character size.</summary>
    Unknown = 0,

    /// <summary>A type-3 record, or SAVEVARS.DAT's copy of it.</summary>
    Player,

    /// <summary>A type-18 record.</summary>
    Monster
}

/// <summary>
///     The 791-byte body shared by a SAVETREE Player (type 3) and Monster (type 18) record, and
///     copied verbatim to the head of SAVEVARS.DAT. Offsets in the comments are UESP's, counted from
///     the record's u32 length word; the constants here are BODY-relative (UESP minus 65).
///     <para>
///         ⚑ <b>Measured on SAVE0 (2026-09-07), against oracles OUTSIDE the file.</b> The name reads
///         "Biggus Dickus", the eight attributes at +97 read 67 50 35 40 52 55 50 75 (Str, Int, Wil,
///         Agi, End, Per, Spd, Luc — the order UESP gives) and the same eight again at +129 (base);
///         those are the values the user stated for the character. The class at +443 and +499
///         reads "Monk", race 1 = Breton, level 1, wounds 6 / 70 / 70 — and the save's own log text
///         says "You now have 22 out of 70", so the 70 is corroborated by the game's message, not
///         only by the layout. The RecordID of the player is 50000 (0xC350), as UESP states.
///     </para>
///     <para>
///         ⚑ SAVEVARS.DAT opens with a byte-identical copy of this body: SAVEVARS bytes 0..790
///         equal the tree player record's body byte for byte, all 791.
///     </para>
///     <para>
///         ⛔ <b>That does NOT settle UESP's 787 against 791 — the control cannot discriminate.</b>
///         The body's last 48 bytes (743..790) are zero, SAVEVARS 791..1050 is a 260-byte zero gap,
///         and SAVEVARS' first non-zero byte after the copy is at 1,051 (the Misc block). A copy of
///         ANY length from 743 to 1,051 is therefore byte-for-byte identical on this fixture, so the
///         bytes that would separate 787 from 791 carry no signal; and "the longest common prefix is
///         791" is additionally an artefact of capping the comparison at the body length — it could
///         not have come out higher. An earlier revision of this comment (and of
///         <see cref="BattlespireSaveVars" />) filed 787 in the refuted list. It belongs in the OPEN
///         list: the copy length is undecided until a save is measured whose player body has
///         non-zero bytes in 787..790. What IS established is only the byte-identity over 0..790.
///     </para>
///     <para>
///         ⚠ <b>SAVEVARID at +803 is the StaticEnemy ordinal PLUS ONE</b>, 0 meaning none: 56/56 of
///         the monsters that have a StaticEnemy entry carry ordinal + 1, 0/56 carry the ordinal, and
///         all 41 monsters without an entry carry 0. UESP's "record ordinal" is off by one.
///     </para>
///     <para>
///         ⚠⚠ <b>+803 is a MONSTER field only</b>, so it is exposed through
///         <see cref="SaveVarId" />, which is null on a player. The fixture's player carries
///         0xED07ED00 there (bytes <c>FF 80 00 00 | ED 07 ED 07</c> at +800..+807 — two 2,029 words
///         behind an 0x80FF), while the 97 monsters carry exactly 0..56: 0 on 41 and each of 1..56
///         once. Reading the player's word as a SAVEVARID yields the impossible ordinal
///         3,976,719,615. What that player-only region holds is NOT established.
///     </para>
///     <para>
///         ⚠ Hotkey bytes (+575) hold the stored spell id (<c>SpellList + 1</c>; the fixture's F1 = 21
///         = Cure Health) or 64..71 to point at the item RecordIDs at +808.
///     </para>
///     <para>
///         ⛔ CharacterFlags bit 11: UESP labels it a second "is female" bit, but the fixture's MALE
///         player (name, and the PCMale=1 / PCFemale=0 globals) has flags 0xC44 — bits 2, 6, 10 and
///         11 set. One fixture contradicts the label; it is not settled, so no gender property is
///         derived from the flags here.
///     </para>
///     <para>
///         ⚠ EnemyType (+655) indexes <see cref="BattlespireSaveLists.EnemyNames" /> directly and
///         agrees with the stored name on 97/97 monsters (41 Scamp, 42 Vermai, 14 Dremora). Race
///         (+670) and Hair/Eyes/Mouth (+639) are player-only fields.
///     </para>
/// </summary>
internal sealed class BattlespireSaveCharacter
{
    /// <summary>Bytes in the body (record total 856 minus the 65-byte header).</summary>
    public const int BodyLength = 791;

    /// <summary>Attributes per character, in UESP order: Str, Int, Wil, Agi, End, Per, Spd, Luc.</summary>
    public const int AttributeCount = 8;

    /// <summary>Skills per character, in <see cref="BattlespireSaveLists.SkillNames" /> order.</summary>
    public const int SkillCount = 21;

    /// <summary>Armour slots at +681: helmet, pauldron, cuirass, greave, gauntlet, boot, shield.</summary>
    public const int ArmorClassCount = 7;

    /// <summary>Hotkeys F1..F8.</summary>
    public const int HotkeyCount = 8;

    /// <summary>Skill picks at +487: 3 primary, 3 major, 6 minor.</summary>
    public const int SkillPickCount = 12;

    private const int NameLength = 32;
    private const int ClassNameLength = 24;

    private const int NameOffset = 0; // +65
    private const int AttributesOffset = 32; // +97
    private const int BaseAttributesOffset = 64; // +129
    private const int SpellPointsOffset = 96; // +161 u16 current, +163 max, +165 base
    private const int WoundsOffset = 102; // +167 s32 current, +171 max, +175 base
    private const int SkillsOffset = 114; // +179, 21 x 12 bytes, ends +431
    private const int ShieldEffectOffset = 366; // +431
    private const int ActiveSpellsOffset = 370; // +435
    private const int EnemyLevelOffset = 374; // +439
    private const int ClassNameOffset = 378; // +443
    private const int AdvantagesOffset = 402; // +467
    private const int SkillPicksOffset = 422; // +487
    private const int BaseClassNameOffset = 434; // +499
    private const int BaseAdvantagesOffset = 458; // +523
    private const int BaseSkillPicksOffset = 478; // +543
    private const int CombatStateOffset = 494; // +559 s32, -2 on the player
    private const int HotkeysOffset = 510; // +575
    private const int AvailableSkillPointsOffset = 522; // +587
    private const int FlagsOffset = 550; // +615
    private const int TargetOffset = 554; // +619
    private const int TeamOffset = 558; // +623
    private const int GoalOffset = 562; // +627
    private const int HairOffset = 574; // +639 hair, +640 eyes, +641 mouth
    private const int EnemyTypeOffset = 590; // +655
    private const int EquippedWeaponItemIdOffset = 602; // +667 u16
    private const int RaceOffset = 605; // +670
    private const int EquippedWeaponRecordIdOffset = 608; // +673
    private const int ArmorClassOffset = 616; // +681, 7 x u32
    private const int ViewPitchOffset = 668; // +733 s16
    private const int LevelOffset = 671; // +736
    private const int AnchorOffset = 688; // +753, 3 x f32
    private const int WaterBreathingOffset = 704; // +769
    private const int IsDeadOffset = 734; // +799
    private const int SaveVarIdOffset = 738; // +803
    private const int HotkeyItemRecordIdsOffset = 743; // +808, 8 x u32

    private BattlespireSaveCharacter()
    {
    }

    /// <summary>Which record this body came from — the +803 field is monster-only.</summary>
    public required BattlespireSaveCharacterKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>Current attributes, in UESP order (Str, Int, Wil, Agi, End, Per, Spd, Luc).</summary>
    public required IReadOnlyList<uint> Attributes { get; init; }

    /// <summary>Base attributes, same order.</summary>
    public required IReadOnlyList<uint> BaseAttributes { get; init; }

    public required ushort SpellPoints { get; init; }

    public required ushort SpellPointsMax { get; init; }

    public required ushort SpellPointsBase { get; init; }

    public required int Wounds { get; init; }

    public required int WoundsMax { get; init; }

    public required int WoundsBase { get; init; }

    /// <summary>The 21 skills in <see cref="BattlespireSaveLists.SkillNames" /> order.</summary>
    public required IReadOnlyList<BattlespireSaveSkill> Skills { get; init; }

    /// <summary>Shield spell magnitude.</summary>
    public required uint ShieldEffect { get; init; }

    /// <summary>Active-spell bit array.</summary>
    public required uint ActiveSpells { get; init; }

    /// <summary>Level, as stored for monsters at +439.</summary>
    public required uint EnemyLevel { get; init; }

    public required string ClassName { get; init; }

    public required BattlespireSaveAdvantages Advantages { get; init; }

    /// <summary>Skill picks: 3 primary, 3 major, 6 minor, as SkillList indices.</summary>
    public required IReadOnlyList<byte> SkillPicks { get; init; }

    public required string BaseClassName { get; init; }

    public required BattlespireSaveAdvantages BaseAdvantages { get; init; }

    public required IReadOnlyList<byte> BaseSkillPicks { get; init; }

    /// <summary>+559: -2 on the player, 0 on monsters until they attack.</summary>
    public required int CombatState { get; init; }

    /// <summary>F1..F8: a stored spell id (<c>list + 1</c>) or 64..71 for <see cref="HotkeyItemRecordIds" />.</summary>
    public required IReadOnlyList<byte> Hotkeys { get; init; }

    public required uint AvailableSkillPoints { get; init; }

    /// <summary>+615 character flags — see the class remarks on bit 11.</summary>
    public required uint Flags { get; init; }

    public required uint Target { get; init; }

    public required uint Team { get; init; }

    public required uint Goal { get; init; }

    public required byte Hair { get; init; }

    public required byte Eyes { get; init; }

    public required byte Mouth { get; init; }

    /// <summary>EnemyList index (0 Scamp, 1 Vermai, 2 Dremora ...).</summary>
    public required uint EnemyType { get; init; }

    /// <summary>+667: WeaponList value; 15 = hand to hand on the fixture.</summary>
    public required ushort EquippedWeaponItemId { get; init; }

    /// <summary>RaceList index, player only: 1 = Breton.</summary>
    public required byte Race { get; init; }

    public required uint EquippedWeaponRecordId { get; init; }

    /// <summary>Armour class per slot: helmet, pauldron, cuirass, greave, gauntlet, boot, shield.</summary>
    public required IReadOnlyList<uint> ArmorClass { get; init; }

    public required short ViewPitch { get; init; }

    /// <summary>Player level at +736.</summary>
    public required uint Level { get; init; }

    /// <summary>Teleport anchor; all zero when none is set.</summary>
    public required (float X, float Y, float Z) Anchor { get; init; }

    public required uint WaterBreathingExpiresAt { get; init; }

    /// <summary>
    ///     +799, set on dead monsters (1 of 97 on the fixture). The player's byte is 0, which is
    ///     consistent with "alive" but does not by itself show the field means the same there.
    /// </summary>
    public required bool IsDead { get; init; }

    /// <summary>
    ///     +803 raw, whatever the kind — the player's word is not a SAVEVARID (see the class
    ///     remarks). Use <see cref="SaveVarId" /> for the decoded monster field.
    /// </summary>
    public required uint SaveVarWord { get; init; }

    /// <summary>Item RecordIDs behind hotkeys mapped to items (hotkey byte 64..71).</summary>
    public required IReadOnlyList<uint> HotkeyItemRecordIds { get; init; }

    /// <summary>
    ///     +803 on a MONSTER: the StaticEnemy ordinal + 1 in SAVEVARS, 0 when it has no entry.
    ///     Null on a player or an untyped body, where the word is something else.
    /// </summary>
    public uint? SaveVarId => Kind == BattlespireSaveCharacterKind.Monster ? SaveVarWord : null;

    /// <summary>The StaticEnemy ordinal this character's <see cref="SaveVarId" /> points at, or null for none.</summary>
    public int? StaticEnemyOrdinal => SaveVarId is { } id && id != 0 ? (int)(id - 1) : null;

    public string? RaceName => BattlespireSaveLists.RaceName(Race);

    public string? EnemyTypeName => BattlespireSaveLists.EnemyName((int)EnemyType);

    /// <summary>
    ///     Parses a body of at least <see cref="BodyLength" /> bytes. <paramref name="kind" /> comes
    ///     from the record type and gates the monster-only +803 field.
    /// </summary>
    public static BattlespireSaveCharacter Parse(ReadOnlySpan<byte> body, string name,
        BattlespireSaveCharacterKind kind)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (body.Length < BodyLength)
        {
            throw new InvalidDataException($"{name}: a character body needs {BodyLength} bytes, got {body.Length}.");
        }

        var skills = new BattlespireSaveSkill[SkillCount];
        for (var i = 0; i < SkillCount; i++)
        {
            var at = SkillsOffset + i * 12;
            skills[i] = new BattlespireSaveSkill(U32(body, at), U32(body, at + 4), U32(body, at + 8));
        }

        return new BattlespireSaveCharacter
        {
            Kind = kind,
            Name = ReadFixed(body, NameOffset, NameLength),
            Attributes = ReadU32s(body, AttributesOffset, AttributeCount),
            BaseAttributes = ReadU32s(body, BaseAttributesOffset, AttributeCount),
            SpellPoints = U16(body, SpellPointsOffset),
            SpellPointsMax = U16(body, SpellPointsOffset + 2),
            SpellPointsBase = U16(body, SpellPointsOffset + 4),
            Wounds = S32(body, WoundsOffset),
            WoundsMax = S32(body, WoundsOffset + 4),
            WoundsBase = S32(body, WoundsOffset + 8),
            Skills = skills,
            ShieldEffect = U32(body, ShieldEffectOffset),
            ActiveSpells = U32(body, ActiveSpellsOffset),
            EnemyLevel = U32(body, EnemyLevelOffset),
            ClassName = ReadFixed(body, ClassNameOffset, ClassNameLength),
            Advantages = BattlespireSaveAdvantages.Read(body[AdvantagesOffset..]),
            SkillPicks = body.Slice(SkillPicksOffset, SkillPickCount).ToArray(),
            BaseClassName = ReadFixed(body, BaseClassNameOffset, ClassNameLength),
            BaseAdvantages = BattlespireSaveAdvantages.Read(body[BaseAdvantagesOffset..]),
            BaseSkillPicks = body.Slice(BaseSkillPicksOffset, SkillPickCount).ToArray(),
            CombatState = S32(body, CombatStateOffset),
            Hotkeys = body.Slice(HotkeysOffset, HotkeyCount).ToArray(),
            AvailableSkillPoints = U32(body, AvailableSkillPointsOffset),
            Flags = U32(body, FlagsOffset),
            Target = U32(body, TargetOffset),
            Team = U32(body, TeamOffset),
            Goal = U32(body, GoalOffset),
            Hair = body[HairOffset],
            Eyes = body[HairOffset + 1],
            Mouth = body[HairOffset + 2],
            EnemyType = U32(body, EnemyTypeOffset),
            EquippedWeaponItemId = U16(body, EquippedWeaponItemIdOffset),
            Race = body[RaceOffset],
            EquippedWeaponRecordId = U32(body, EquippedWeaponRecordIdOffset),
            ArmorClass = ReadU32s(body, ArmorClassOffset, ArmorClassCount),
            ViewPitch = BinaryPrimitives.ReadInt16LittleEndian(body[ViewPitchOffset..]),
            Level = U32(body, LevelOffset),
            Anchor = (F32(body, AnchorOffset), F32(body, AnchorOffset + 4), F32(body, AnchorOffset + 8)),
            WaterBreathingExpiresAt = U32(body, WaterBreathingOffset),
            IsDead = body[IsDeadOffset] != 0,
            SaveVarWord = U32(body, SaveVarIdOffset),
            HotkeyItemRecordIds = ReadU32s(body, HotkeyItemRecordIdsOffset, HotkeyCount)
        };
    }

    private static uint[] ReadU32s(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        var values = new uint[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = U32(bytes, offset + i * 4);
        }

        return values;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static int S32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    }

    private static float F32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    }

    /// <summary>A fixed-width field read to its first NUL (NUL-padded on every retail field seen).</summary>
    internal static string ReadFixed(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        var field = bytes.Slice(offset, length);
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     The 119-byte body of a SAVETREE Spell record (type 9, 184 bytes in all): a spell the player
///     knows, an item enchantment, or a cast effect. Offsets in the comments are UESP's, from the
///     record's length word; the constants are body-relative.
///     <para>
///         ⚑ <b>Measured on SAVE0 (2026-09-07).</b> The two live spells are "Cure Health" with
///         +97 = 21 and +99 = 20, and "Teleport" with 25 / 24. UESP's SpellList has Cure Health at 20
///         and Teleport at 24, so
///         <b>
///             +97 is <c>SpellList + 1</c> and +99 (the icon) is the list
///             value
///         </b>
///         — 2/2, and the reading also makes the potions' stored 40 land on Restore Spell
///         Points, the list's last entry. Header +23 reads 512 on both live spells.
///     </para>
///     <para>
///         ⚠ <b>A THIRD spell-shaped record has type 0</b>: 184 bytes, name "Cure Health", parent =
///         the player, a dynamic id later than the live Cure Health, header +23 = 0 and SP cost 0.
///         The best reading is a tombstoned or expired cast instance. The tree walker keeps it (it
///         walks by length, never by type) and <see cref="BattlespireSaveRecord.AsSpell" /> decodes
///         it by SIZE when the type is 0.
///     </para>
/// </summary>
internal sealed class BattlespireSaveSpell
{
    /// <summary>Bytes in the body (record total 184 minus the 65-byte header).</summary>
    public const int BodyLength = 119;

    /// <summary>+144 value meaning the caster (the player's RecordID).</summary>
    public const uint TargetSelf = 0xC350;

    private const int NameOffset = 0; // +65 char[32]
    private const int NameLength = 32;
    private const int SpellIdOffset = 32; // +97 u8, list + 1
    private const int TargetTypeOffset = 33; // +98 u8
    private const int IconOffset = 34; // +99 u8, list value
    private const int DurationOffset = 35; // +100 base, +104 bonus, +108 bonus level
    private const int MagnitudeOffset = 47; // +112 min, +116 max, +120 bonus min, +124 bonus max, +128 bonus level
    private const int LastCastOffset = 67; // +132 duration, +136 magnitude
    private const int IconBaseOffset = 75; // +140 u16
    private const int ElementOffset = 77; // +142 u8, +143 delivery
    private const int LastCastTargetOffset = 79; // +144
    private const int HeadingOffset = 83; // +148, 3 x f32
    private const int SpCostOffset = 95; // +160 base, +164 current
    private const int SchoolOffset = 103; // +168
    private const int DetonationOffset = 107; // +172

    private BattlespireSaveSpell()
    {
    }

    public required string Name { get; init; }

    /// <summary>+97: the stored id, <c>SpellList + 1</c>.</summary>
    public required byte StoredSpellId { get; init; }

    /// <summary>+98: SpellTargetList (0 self, 1 other, 2 either).</summary>
    public required byte TargetType { get; init; }

    /// <summary>+99: the icon, which is the 0-based SpellList value.</summary>
    public required byte Icon { get; init; }

    public required uint DurationBase { get; init; }

    public required uint DurationBonus { get; init; }

    public required uint DurationBonusLevel { get; init; }

    public required uint MagnitudeMin { get; init; }

    public required uint MagnitudeMax { get; init; }

    public required uint MagnitudeBonusMin { get; init; }

    public required uint MagnitudeBonusMax { get; init; }

    public required uint MagnitudeBonusLevel { get; init; }

    public required uint LastCastDuration { get; init; }

    public required uint LastCastMagnitude { get; init; }

    public required ushort IconBase { get; init; }

    /// <summary>+142: ElementSpellList (0 fire, 1 ice, 2 poison, 3 electricity, 4 magicka).</summary>
    public required byte Element { get; init; }

    /// <summary>+143: SpellDeliveryList.</summary>
    public required byte Delivery { get; init; }

    /// <summary>+144: RecordID of the last target; <see cref="TargetSelf" /> for the caster.</summary>
    public required uint LastCastTarget { get; init; }

    public required (float X, float Y, float Z) Heading { get; init; }

    public required uint SpellPointCostBase { get; init; }

    public required uint SpellPointCostCurrent { get; init; }

    /// <summary>+168: SpellSchoolList.</summary>
    public required uint School { get; init; }

    /// <summary>+172: SpellDetonationList.</summary>
    public required uint Detonation { get; init; }

    /// <summary>The 0-based SpellList index (<see cref="StoredSpellId" /> - 1), or -1 when the stored id is 0.</summary>
    public int SpellListIndex => StoredSpellId - 1;

    /// <summary>The list name for <see cref="StoredSpellId" />, or null.</summary>
    public string? ListName => BattlespireSaveLists.StoredSpellName(StoredSpellId);

    public string? SchoolName => School < (uint)BattlespireSaveLists.SpellSchoolNames.Length
        ? BattlespireSaveLists.SpellSchoolNames[School]
        : null;

    /// <summary>Parses a body of at least <see cref="BodyLength" /> bytes.</summary>
    public static BattlespireSaveSpell Parse(ReadOnlySpan<byte> body, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (body.Length < BodyLength)
        {
            throw new InvalidDataException($"{name}: a spell body needs {BodyLength} bytes, got {body.Length}.");
        }

        return new BattlespireSaveSpell
        {
            Name = BattlespireSaveCharacter.ReadFixed(body, NameOffset, NameLength),
            StoredSpellId = body[SpellIdOffset],
            TargetType = body[TargetTypeOffset],
            Icon = body[IconOffset],
            DurationBase = U32(body, DurationOffset),
            DurationBonus = U32(body, DurationOffset + 4),
            DurationBonusLevel = U32(body, DurationOffset + 8),
            MagnitudeMin = U32(body, MagnitudeOffset),
            MagnitudeMax = U32(body, MagnitudeOffset + 4),
            MagnitudeBonusMin = U32(body, MagnitudeOffset + 8),
            MagnitudeBonusMax = U32(body, MagnitudeOffset + 12),
            MagnitudeBonusLevel = U32(body, MagnitudeOffset + 16),
            LastCastDuration = U32(body, LastCastOffset),
            LastCastMagnitude = U32(body, LastCastOffset + 4),
            IconBase = BinaryPrimitives.ReadUInt16LittleEndian(body[IconBaseOffset..]),
            Element = body[ElementOffset],
            Delivery = body[ElementOffset + 1],
            LastCastTarget = U32(body, LastCastTargetOffset),
            Heading = (F32(body, HeadingOffset), F32(body, HeadingOffset + 4), F32(body, HeadingOffset + 8)),
            SpellPointCostBase = U32(body, SpCostOffset),
            SpellPointCostCurrent = U32(body, SpCostOffset + 4),
            School = U32(body, SchoolOffset),
            Detonation = U32(body, DetonationOffset)
        };
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static float F32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    }
}

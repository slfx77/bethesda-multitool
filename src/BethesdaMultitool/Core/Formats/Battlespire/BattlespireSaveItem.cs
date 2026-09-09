using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     The 755-byte body of a SAVETREE Item record (type 2, 820 bytes in all). Offsets in the
///     comments are UESP's, from the record's length word; the constants are body-relative.
///     <para>
///         ⚑ <b>Measured on SAVE0 (2026-09-07), 323 records.</b> The u16 at +97 indexes UESP's
///         ItemList, and the stored name relates to the list name in exactly three ways — the
///         partition is 275 + 31 + 17 = 323, after trimming the <c>" </c> suffix below:
///         <b>275</b> equal the list name outright; <b>31</b> are the SINGULAR of a plural list
///         entry (Greave/Greaves 10, Gauntlet/Gauntlets 8, Pauldron/Pauldrons 7, Boot/Boots 6 — the
///         list names the type, the record names the one piece); <b>17</b> are id 19 (Clothes),
///         whose name is the BSI sub-type (Arm Bands, Shirt, Pants, Cape) exactly as UESP predicts.
///         ⚠ An earlier revision of this comment said "306/323 equal outright, the other 17 all
///         Clothes": 306 is the equal-OR-singular total and the singular rule was undocumented, so
///         31 records looked like exceptions that were never named. Nothing in the list-lookup code
///         depends on it — <see cref="ItemTypeName" /> is the list name and <see cref="Name" /> the
///         stored one — but any test comparing the two must allow the singular. The u32 at +803 is the <c>ID#</c> of a
///         record in
///         <c>TXT.BSA</c>'s MG0_GEN.TXT / MG2_SPC.TXT item tables (<see cref="BattlespireItemTable" />):
///         105/105 non-zero ids resolve there and the in-save enchantment name at +244 equals the
///         table's Name on 105/105 (9004 "Doht Sigil of Entry", 1014 "of Bandy Interfraction").
///         IsContainer (+131) is 1 on every Sack, Large Chest and Small Chest, 80/80, and nothing
///         else.
///     </para>
///     <para>
///         ⚠ <b>BSI-named armour and clothing carry a two-byte <c>" </c> suffix</b> in the stored
///         name (<c>Shirt" </c>, <c>Helmet" </c>, 59 of 323 records). <see cref="Name" /> trims
///         exactly that suffix; <see cref="RawName" /> keeps it. Whether it is always exactly
///         0x22 0x20 needs a second save.
///     </para>
///     <para>
///         ⚠ The Object body embedded at +133 does NOT mirror the header: its XYZ at +169 equals
///         the header position on only 54/323 records. Carried items hold small vectors there while
///         the header holds the holder's position (or zero). Read the header for placement.
///     </para>
///     <para>
///         ⚠ <b>Weight is stored doubled</b> (Dagger 2, Claymore 24, Arrow 1) — <see cref="Weight" />
///         halves it, <see cref="WeightHalves" /> is the raw word.
///     </para>
///     <para>
///         ⚠ The potion spell at +122 follows the same <c>list + 1</c> convention the Spell record
///         proves (see <see cref="BattlespireSaveLists" />): the three potions storing 40 then read
///         as Restore Spell Points (39), which is unmapped under a 0-based reading. Under that
///         reading one fixture potion (stored 7) names Chameleon, a spell UESP marks removed; under
///         the 0-based reading two potions (5, 22) name removed spells. Neither is contradicted by an
///         oracle — potions are all named "Potion" — so the id is exposed raw as well.
///     </para>
/// </summary>
internal sealed class BattlespireSaveItem
{
    /// <summary>Bytes in the body (record total 820 minus the 65-byte header).</summary>
    public const int BodyLength = 755;

    /// <summary>The suffix retail stores after BSI-named armour and clothing names.</summary>
    public const string BsiNameSuffix = "\" ";

    private const int NameOffset = 0; // +65 char[32]
    private const int NameLength = 32;
    private const int ItemIdOffset = 32; // +97 u16
    private const int FileNameOffset = 34; // +99 char[9]
    private const int FileNameLength = 9;
    private const int ConditionOffset = 43; // +108, +112 max
    private const int ModifierIdOffset = 51; // +116 u16
    private const int WeightOffset = 53; // +118
    private const int SpellIdOffset = 57; // +122 s8
    private const int QuantityOffset = 62; // +127
    private const int IsContainerOffset = 66; // +131 u16
    private const int BaseItemTypeIdOffset = 68; // +133, start of the embedded Object body
    private const int ItemPositionOffset = 104; // +169, 3 x f32
    private const int ItemRotationOffset = 116; // +181, 3 x u32
    private const int EnchantmentNameOffset = 179; // +244 char[33]
    private const int EnchantmentNameLength = 33;
    private const int EnchantmentItemOffset = 212; // +277
    private const int EmbeddedCharacterOffset = 283; // +348 .. +770, attributes/skills block
    private const int EmbeddedCharacterLength = 422;
    private const int ArmorValueOffset = 705; // +770, +771
    private const int CastOnEquipOffset = 717; // +782 .. +786: equip, use, strike, magnitude, element
    private const int ChargesOffset = 722; // +787, +791 max
    private const int EnchantmentIdOffset = 738; // +803

    private BattlespireSaveItem()
    {
    }

    /// <summary>The stored name, suffix and all.</summary>
    public required string RawName { get; init; }

    /// <summary>ItemList index at +97.</summary>
    public required ushort ItemId { get; init; }

    /// <summary>Armour/clothes/weapon art name (MSHRT206, MGREV, mwpns); empty otherwise.</summary>
    public required string FileName { get; init; }

    public required uint Condition { get; init; }

    public required uint ConditionMax { get; init; }

    public required ushort ModifierId { get; init; }

    /// <summary>The raw +118 word: twice the weight.</summary>
    public required uint WeightHalves { get; init; }

    /// <summary>The raw s8 at +122 — a potion's spell, stored as <c>list + 1</c>.</summary>
    public required sbyte StoredSpellId { get; init; }

    public required uint Quantity { get; init; }

    public required bool IsContainer { get; init; }

    /// <summary>+133: the embedded Object body's base type id.</summary>
    public required uint BaseItemTypeId { get; init; }

    /// <summary>+169: the embedded Object body's position — NOT the header's, see the class remarks.</summary>
    public required (float X, float Y, float Z) ItemPosition { get; init; }

    /// <summary>+181: the embedded Object body's pitch, yaw, roll.</summary>
    public required (uint Pitch, uint Yaw, uint Roll) ItemRotation { get; init; }

    /// <summary>+244: the enchantment's name fragment ("of Handfire"); empty when unenchanted.</summary>
    public required string EnchantmentName { get; init; }

    /// <summary>+277: the enchantment's Item class, matching the item table's <c>Item</c> key.</summary>
    public required uint EnchantmentItem { get; init; }

    /// <summary>
    ///     +348..+770: the partial Player-shaped attribute/skill block an enchanted item alters. Laid
    ///     out like the character body from its attributes on (shifted by 251); handed back raw.
    /// </summary>
    public required ReadOnlyMemory<byte> EmbeddedCharacterBlock { get; init; }

    public required byte ArmorValue1 { get; init; }

    public required byte ArmorValue2 { get; init; }

    public required sbyte CastOnEquip { get; init; }

    public required sbyte CastOnUse { get; init; }

    public required sbyte CastOnStrike { get; init; }

    public required sbyte Magnitude { get; init; }

    public required sbyte Element { get; init; }

    public required uint Charges { get; init; }

    public required uint ChargesMax { get; init; }

    /// <summary>+803: the <c>ID#</c> in TXT.BSA's item tables, 0 when unenchanted.</summary>
    public required uint EnchantmentId { get; init; }

    /// <summary><see cref="RawName" /> with the BSI <c>" </c> suffix removed.</summary>
    public string Name => RawName.EndsWith(BsiNameSuffix, StringComparison.Ordinal)
        ? RawName[..^BsiNameSuffix.Length]
        : RawName;

    /// <summary>The ItemList name for <see cref="ItemId" />, or null when out of range.</summary>
    public string? ItemTypeName => BattlespireSaveLists.ItemName(ItemId);

    /// <summary>The weight in the game's units (the stored word halved).</summary>
    public double Weight => WeightHalves / 2.0;

    /// <summary>The potion spell under the <c>list + 1</c> convention, or null.</summary>
    public string? SpellName => BattlespireSaveLists.StoredSpellName(StoredSpellId);

    public bool IsEnchanted => EnchantmentId != 0;

    /// <summary>Parses a body of at least <see cref="BodyLength" /> bytes.</summary>
    public static BattlespireSaveItem Parse(ReadOnlyMemory<byte> body, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (body.Length < BodyLength)
        {
            throw new InvalidDataException($"{name}: an item body needs {BodyLength} bytes, got {body.Length}.");
        }

        var b = body.Span;
        return new BattlespireSaveItem
        {
            RawName = BattlespireSaveCharacter.ReadFixed(b, NameOffset, NameLength),
            ItemId = BinaryPrimitives.ReadUInt16LittleEndian(b[ItemIdOffset..]),
            FileName = BattlespireSaveCharacter.ReadFixed(b, FileNameOffset, FileNameLength),
            Condition = U32(b, ConditionOffset),
            ConditionMax = U32(b, ConditionOffset + 4),
            ModifierId = BinaryPrimitives.ReadUInt16LittleEndian(b[ModifierIdOffset..]),
            WeightHalves = U32(b, WeightOffset),
            StoredSpellId = (sbyte)b[SpellIdOffset],
            Quantity = U32(b, QuantityOffset),
            IsContainer = BinaryPrimitives.ReadUInt16LittleEndian(b[IsContainerOffset..]) != 0,
            BaseItemTypeId = U32(b, BaseItemTypeIdOffset),
            ItemPosition = (F32(b, ItemPositionOffset), F32(b, ItemPositionOffset + 4), F32(b, ItemPositionOffset + 8)),
            ItemRotation = (U32(b, ItemRotationOffset), U32(b, ItemRotationOffset + 4), U32(b, ItemRotationOffset + 8)),
            EnchantmentName = BattlespireSaveCharacter.ReadFixed(b, EnchantmentNameOffset, EnchantmentNameLength),
            EnchantmentItem = U32(b, EnchantmentItemOffset),
            EmbeddedCharacterBlock = body.Slice(EmbeddedCharacterOffset, EmbeddedCharacterLength),
            ArmorValue1 = b[ArmorValueOffset],
            ArmorValue2 = b[ArmorValueOffset + 1],
            CastOnEquip = (sbyte)b[CastOnEquipOffset],
            CastOnUse = (sbyte)b[CastOnEquipOffset + 1],
            CastOnStrike = (sbyte)b[CastOnEquipOffset + 2],
            Magnitude = (sbyte)b[CastOnEquipOffset + 3],
            Element = (sbyte)b[CastOnEquipOffset + 4],
            Charges = U32(b, ChargesOffset),
            ChargesMax = U32(b, ChargesOffset + 4),
            EnchantmentId = U32(b, EnchantmentIdOffset)
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

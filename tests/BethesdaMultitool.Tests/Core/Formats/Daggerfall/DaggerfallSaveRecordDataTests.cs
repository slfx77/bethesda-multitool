using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic vectors for the three decoded record bodies: Item (107 bytes), Spell (89) and
///     Character (635, career block included). Every expectation is a literal written into the
///     buffer by hand, never recomputed from the parser.
/// </summary>
public class DaggerfallSaveRecordDataTests
{
    private static void WriteName(Span<byte> field, string text)
    {
        field.Clear();
        Encoding.ASCII.GetBytes(text).CopyTo(field);
    }

    /// <summary>An item body carrying the fixture's Parchment values.</summary>
    internal static byte[] ItemBody()
    {
        var data = new byte[DaggerfallSaveItem.DataLength];
        WriteName(data.AsSpan(0, DaggerfallSaveItem.NameLength), "Parchment");
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 360);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(42), 0x0011);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(44), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(46), 60);
        data[49] = 12;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(50), 205);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(52), 206);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(54), 2);
        data[56] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(57), 250);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(61), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(63), 1_020);
        data[65] = 2;
        data[66] = 1;
        for (var i = 0; i < DaggerfallSaveItem.EnchantmentSlots; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(67 + i * 4), -1);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(69 + i * 4), -1);
        }

        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(67), 7);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(69), 300);
        return data;
    }

    [Fact]
    public void Item_ReadsEveryField()
    {
        var item = DaggerfallSaveItem.Parse(ItemBody());

        Assert.Equal("Parchment", item.Name);
        Assert.Equal(9, item.Group);
        Assert.Equal(5, item.Index);
        Assert.Equal(360u, item.Value);
        Assert.Equal(0x0011, item.Flags);
        Assert.Equal(40, item.CurrentCondition);
        Assert.Equal(60, item.MaxCondition);
        Assert.Equal(12, item.TypeDependent);
        Assert.Equal(205, item.Image1);
        Assert.Equal(206, item.Image2);
        Assert.Equal(2, item.Material);
        Assert.Equal(3, item.Color);
        Assert.Equal(250u, item.Weight);
        Assert.Equal(40, item.EnchantmentPoints);
        Assert.Equal(1_020, item.Message);
        Assert.Equal(2, item.Variants);
        Assert.Equal(1, item.DrawOrder);
    }

    [Fact]
    public void Item_ReadsTenEnchantmentSlotsAndMarksTheUnusedOnes()
    {
        var item = DaggerfallSaveItem.Parse(ItemBody());

        Assert.Equal(10, item.Enchantments.Count);
        Assert.True(item.Enchantments[0].IsSet);
        Assert.Equal(7, item.Enchantments[0].Type);
        Assert.Equal(300, item.Enchantments[0].Param);
        for (var i = 1; i < 10; i++)
        {
            Assert.False(item.Enchantments[i].IsSet);
            Assert.Equal(-1, item.Enchantments[i].Type);
        }
    }

    /// <summary>A name that fills the field with no NUL must still read back whole.</summary>
    [Fact]
    public void Item_ReadsAnUnterminatedName()
    {
        var data = ItemBody();
        WriteName(data.AsSpan(0, DaggerfallSaveItem.NameLength), new string('x', 32));

        Assert.Equal(new string('x', 32), DaggerfallSaveItem.Parse(data).Name);
    }

    [Fact]
    public void Item_RejectsAShortBody()
    {
        Assert.Throws<InvalidDataException>(() =>
            DaggerfallSaveItem.Parse(new byte[DaggerfallSaveItem.DataLength - 1]));
    }

    /// <summary>
    ///     A spell body shaped like the fixture's "Free Action", which is SPELLS.STD record 9 and
    ///     carries the id byte 10. ⚠ Those two numbers happen to sit one apart on THIS spell only —
    ///     see <see cref="Spell_ReadsEveryField" />; the id is not a position.
    /// </summary>
    internal static byte[] SpellBody()
    {
        var data = new byte[DaggerfallSaveSpell.DataLength];
        data[0] = 22;
        data[1] = 255;
        data[2] = 255;
        data[3] = 255;
        data[4] = 255;
        data[5] = 255;
        data[6] = 1;
        data[7] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 33);
        data[14] = 5;
        data[15] = 1;
        data[16] = 2;
        data[23] = 40;
        data[24] = 3;
        data[25] = 4;
        data[32] = 6;
        data[33] = 7;
        data[34] = 8;
        data[35] = 9;
        data[36] = 10;
        WriteName(data.AsSpan(47, DaggerfallSaveSpell.NameLength), "Free Action");
        data[72] = 14;
        data[73] = 10;
        return data;
    }

    [Fact]
    public void Spell_ReadsEveryField()
    {
        var spell = DaggerfallSaveSpell.Parse(SpellBody());

        Assert.Equal("Free Action", spell.Name);
        Assert.Equal(1, spell.Element);
        Assert.Equal(0, spell.RangeType);
        Assert.Equal(33, spell.Cost);
        Assert.Equal(14, spell.Icon);
        // ⛔ 10 is the spell ID stored at data[73] and NOTHING MORE. It is NOT the one-based
        // position of the matching SPELLS.STD record: measured over the retail SAVE0's six spells
        // the id/position pairs are 10/9, 44/41, 7/6, 44/41, 29/27 and 10/9, so "position + 1"
        // holds on 3 of 6, and STD id 58 is carried by two different records. A spell is located
        // by SEARCHING SPELLS.STD for a byte-identical 89-byte record, never by indexing.
        Assert.Equal(10, spell.Index);

        Assert.Equal(3, spell.Effects.Count);
        Assert.True(spell.Effects[0].IsSet);
        Assert.Equal(22, spell.Effects[0].EffectType);
        Assert.False(spell.Effects[1].IsSet);
        Assert.False(spell.Effects[2].IsSet);

        Assert.Equal(new DaggerfallSaveSpellScaling(5, 1, 2), spell.Durations[0]);
        Assert.Equal(new DaggerfallSaveSpellScaling(40, 3, 4), spell.Chances[0]);
        Assert.Equal(new DaggerfallSaveSpellMagnitude(6, 7, 8, 9, 10), spell.Magnitudes[0]);
    }

    [Fact]
    public void Spell_RejectsAShortBody()
    {
        Assert.Throws<InvalidDataException>(() =>
            DaggerfallSaveSpell.Parse(new byte[DaggerfallSaveSpell.DataLength - 1]));
    }

    /// <summary>A 74-byte career block shaped like CLASS12.CFG, "Monk".</summary>
    internal static byte[] CareerBlock()
    {
        var block = new byte[DaggerfallCareer.Length];
        block[16] = 30;
        block[17] = 34;
        block[18] = 20;
        block[19] = 17;
        block[20] = 0;
        block[21] = 32;
        new byte[] { 31, 29, 33, 28, 3, 18 }.CopyTo(block, 22);
        WriteName(block.AsSpan(28, 16), "Monk");
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(52), 14);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(54), 300);
        ushort[] attributes = [50, 45, 45, 62, 48, 42, 58, 50];
        for (var i = 0; i < attributes.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(58 + i * 2), attributes[i]);
        }

        return block;
    }

    /// <summary>A character body carrying the fixture's "Hans" values.</summary>
    internal static byte[] CharacterBody()
    {
        var data = new byte[DaggerfallSaveCharacter.DataLength];
        WriteName(data.AsSpan(0, 32), "Hans");
        short[] attributes = [65, 55, 55, 65, 56, 50, 60, 50];
        for (var i = 0; i < attributes.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32 + i * 2), attributes[i]);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(48 + i * 2), attributes[i]);
        }

        data[64] = 8;
        data[65] = 1;
        data[67] = 2;
        for (var i = 0; i < 7; i++)
        {
            data[68 + i] = 100;
        }

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x58), 162);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x5C), 39);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x7C), 22);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x7E), 39);
        data[0x81] = 1;
        data[0x83] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x85), 102);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x8D), 27);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x8F), 27);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x91), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x9B), 7_491);

        // Skill 30 (Hand-to-Hand) 33/20, skill 34 (Critical Strike) 31/20, skill 20 (Dodging) 30/11.
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x9D + 30 * 6), 33);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x9D + 30 * 6 + 2), 20);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x9D + 34 * 6), 31);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x9D + 34 * 6 + 2), 20);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x9D + 20 * 6), 30);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x9D + 20 * 6 + 2), 11);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x16F + 17 * 4), 0x006429DD);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x16F + 24 * 4), 0x0064273D);

        data[0x1F2] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1F9), 51_250);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1FD), 524_043);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20D), 524_042);
        CareerBlock().CopyTo(data, 0x230);
        return data;
    }

    [Fact]
    public void Character_ReadsTheScalarFields()
    {
        var character = DaggerfallSaveCharacter.Parse(CharacterBody());

        Assert.Equal("Hans", character.Name);
        Assert.Equal<short>([65, 55, 55, 65, 56, 50, 60, 50], character.CurrentAttributes);
        Assert.Equal(character.CurrentAttributes, character.BaseAttributes);
        Assert.False(character.IsFemale);
        Assert.Equal(1, character.TransportFlags);
        Assert.Equal(2, character.RaceByte);
        Assert.Equal("Nord", character.RaceName);
        Assert.Equal(162, character.StartingLevelUpSkillSum);
        Assert.Equal(39, character.BaseHealth);
        Assert.Equal(22, character.CurrentHealth);
        Assert.Equal(39, character.MaxHealth);
        Assert.Equal(1, character.Level);
        Assert.Equal("High", character.ReflexesName);
        Assert.Equal(102u, character.Gold);
        Assert.Equal(27, character.SpellPoints);
        Assert.Equal(10, character.ReputationCommoners);
        Assert.Equal(7_491, character.Fatigue);
        Assert.Equal(2, character.OriginalRaceByte);
    }

    /// <summary>
    ///     The three offsets the survey note placed one byte too low. Reading at +0x1F9/+0x1FD/
    ///     +0x20D gives 51,250 and the two game-time stamps; one byte lower gives 13,120,000 and
    ///     134,155,008 on the retail bytes, so this vector fails if the fields ever shift.
    /// </summary>
    [Fact]
    public void Character_ReadsTheTailFieldsAtTheMeasuredOffsets()
    {
        var character = DaggerfallSaveCharacter.Parse(CharacterBody());

        Assert.Equal(51_250u, character.Unknown1F9);
        Assert.Equal(524_043u, character.TimeStamp1FD);
        Assert.Equal(524_042u, character.TimeStamp20D);
        Assert.Equal("22:03, 4 Morning Star 3E405", DaggerfallGameTime.FromMinutes(character.TimeStamp1FD).ToString());
    }

    [Fact]
    public void Character_ReadsThirtyFiveSkillsInIdOrder()
    {
        var character = DaggerfallSaveCharacter.Parse(CharacterBody());

        Assert.Equal(35, character.Skills.Count);
        Assert.Equal("Medical", character.Skills[0].Name);
        Assert.Equal("Hand-to-Hand", character.Skills[30].Name);
        Assert.Equal(33, character.Skills[30].Value);
        Assert.Equal(20, character.Skills[30].UseCounter);
        Assert.Equal("Critical Strike", character.Skills[34].Name);
        Assert.Equal(31, character.Skills[34].Value);
        Assert.Equal(30, character.Skills[20].Value);
    }

    [Fact]
    public void Character_ReadsTwentySevenEquipmentSlots()
    {
        var character = DaggerfallSaveCharacter.Parse(CharacterBody());

        Assert.Equal(27, character.EquippedItemIds.Count);
        Assert.Equal(0x006429DDu, character.EquippedItemIds[17]);
        Assert.Equal(0x0064273Du, character.EquippedItemIds[24]);
        Assert.Equal(2, character.EquippedItemIds.Count(id => id != 0));
    }

    /// <summary>
    ///     The career's three primaries, two best majors and best minor sum to the record's own
    ///     starting level-up skill sum: 33 + 31 + 30 + 25 + 21 + 22 = 162. Only the primaries are
    ///     populated in this synthetic body, so the pin here is the SLOT layout — the retail
    ///     arithmetic is checked in the Bucket-B suite.
    /// </summary>
    [Fact]
    public void Career_ReadsTheClassBlock()
    {
        var character = DaggerfallSaveCharacter.Parse(CharacterBody());

        Assert.Equal("Monk", character.Career.Name);
        Assert.Equal<byte>([30, 34, 20], character.Career.PrimarySkillIds);
        Assert.Equal<byte>([17, 0, 32], character.Career.MajorSkillIds);
        Assert.Equal<byte>([31, 29, 33, 28, 3, 18], character.Career.MinorSkillIds);
        Assert.Equal(14, character.Career.HitPointsPerLevel);
        Assert.Equal(300u, character.Career.AdvancementMultiplier);
        Assert.Equal<ushort>([50, 45, 45, 62, 48, 42, 58, 50], character.Career.Attributes);
        Assert.Equal(DaggerfallCareer.Length, character.Career.Raw.Length);
        Assert.Equal(16, character.Career.Flags.Length);
    }

    [Fact]
    public void Character_DescribesItselfInOneLine()
    {
        var character = DaggerfallSaveCharacter.Parse(CharacterBody());

        Assert.Equal(
            "Hans, level 1 Nord Monk (male), health 22/39, spell points 27/27, 102 gold, high reflexes",
            character.Describe());
    }

    [Fact]
    public void Character_RejectsAShortBody()
    {
        Assert.Throws<InvalidDataException>(() =>
            DaggerfallSaveCharacter.Parse(new byte[DaggerfallSaveCharacter.DataLength - 1]));
    }

    [Fact]
    public void Career_RejectsABlockOfTheWrongLength()
    {
        Assert.Throws<ArgumentException>(() => DaggerfallCareer.Parse(new byte[DaggerfallCareer.Length - 1]));
    }
}
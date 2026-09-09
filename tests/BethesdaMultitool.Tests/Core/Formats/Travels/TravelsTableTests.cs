using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for the shared Elder Scrolls Travels data tables (Stormhold 2003 and
///     Dawnstar 2004 ship the same layouts). Everything here is built byte by byte so the
///     big-endian, Java-stream reading is pinned independently of the retail files — and so each
///     rejection path the parsers carry is exercised, since these formats have no magic number and
///     the tiling arithmetic is the only thing standing between a right and a wrong decode.
/// </summary>
public sealed class TravelsTableTests
{
    private const string Name = "synthetic.dat";

    // ---------------------------------------------------------------- TravelsDataReader

    [Fact]
    public void Reader_ReadsJavaPrimitivesBigEndianWithJavaSignedness()
    {
        var bytes = new Buf().Raw(0xFF).Raw(0xFF, 0xFE).Raw(0x80, 0x01).Raw(0xFF, 0xFF, 0xFF, 0xFF).Done();
        var reader = new TravelsDataReader(bytes, Name);

        Assert.Equal(-1, reader.ReadInt8());
        Assert.Equal(-2, reader.ReadInt16());
        Assert.Equal(0x8001, reader.ReadUInt16());
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(9, reader.Position);
        Assert.Equal(0, reader.Remaining);
        reader.ExpectEnd();
    }

    [Fact]
    public void Reader_ReadUtf_TakesAByteLengthAndDecodesUtf8()
    {
        // U+2019 is three bytes but one character: the prefix counts BYTES, which is the trap a
        // character-counting reader falls into.
        var bytes = new Buf().Utf("Varus’ Victory").Done();
        var reader = new TravelsDataReader(bytes, Name);

        Assert.Equal("Varus’ Victory", reader.ReadUtf());
        Assert.Equal(bytes.Length, reader.Position);
    }

    [Fact]
    public void Reader_ReadUtf_TruncatedPayload_ThrowsNamingFileAndPosition()
    {
        var bytes = new Buf().U16(8).Raw(0x41, 0x42).Done();
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new TravelsDataReader(bytes, Name);
            reader.ReadUtf();
        });

        Assert.Contains(Name, ex.Message, StringComparison.Ordinal);
        Assert.Contains("byte 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ReadUtf_InvalidUtf8_Throws()
    {
        var bytes = new Buf().U16(2).Raw(0xC0, 0x80).Done();
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new TravelsDataReader(bytes, Name);
            reader.ReadUtf();
        });

        Assert.Contains("not valid UTF-8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ReadUtfList16_NegativeCount_Throws()
    {
        var bytes = new Buf().I16(-1).Done();
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new TravelsDataReader(bytes, Name);
            reader.ReadUtfList16();
        });

        Assert.Contains("negative", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ReadUtfArray_CountBeyondTheFile_ThrowsBeforeAllocating()
    {
        var bytes = new Buf().Utf("a").Done();
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new TravelsDataReader(bytes, Name);
            reader.ReadUtfArray(1_000_000);
        });

        Assert.Contains("past the", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ExpectEnd_TrailingBytes_Throws()
    {
        var bytes = new Buf().I16(1).Raw(0x00).Done();
        var ex = Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new TravelsDataReader(bytes, Name);
            reader.ReadInt16();
            reader.ExpectEnd();
        });

        Assert.Contains("trailing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ReadPastEnd_Throws()
    {
        var bytes = new Buf().Raw(0x01, 0x02).Done();
        Assert.Throws<InvalidDataException>(() =>
        {
            var reader = new TravelsDataReader(bytes, Name);
            reader.ReadInt32();
        });
    }

    // ---------------------------------------------------------------- charin.dat

    private static Buf CharacterHeader(int classCount, int skillCount = TravelsCharacterTable.SkillCount)
    {
        var buf = new Buf()
            .List16("Level", "Health")
            .List16("Strength", "Strength Increases")
            .List16(Enumerable.Range(0, classCount).Select(i => $"Class{i}").ToArray())
            .List16("Redguard", "Nord", "Breton")
            .List16(Enumerable.Range(0, skillCount).Select(i => $"Skill{i}").ToArray());

        for (var i = 0; i < skillCount; i++)
        {
            buf.I16(i * 2);
        }

        return buf;
    }

    private static void CharacterRow(Buf buf, int index)
    {
        buf.I16(index).I16(index % 3);
        for (var a = 0; a < TravelsCharacterTable.AttributeCount; a++)
        {
            buf.I16(30 + 10 * a);
        }

        buf.I16(4).I16(11).I16(12);
        for (var s = 0; s < TravelsCharacterTable.SkillCount; s++)
        {
            buf.I16(s % 5).I16(35 + s);
        }
    }

    [Fact]
    public void CharacterTable_ParsesListsGoverningAttributesAndClassRows()
    {
        var buf = CharacterHeader(2);
        CharacterRow(buf, 0);
        CharacterRow(buf, 1);

        var table = TravelsCharacterTable.Parse(buf.Done(), Name);

        Assert.Equal(new[] { "Level", "Health" }, table.StatLabels);
        Assert.Equal(new[] { "Redguard", "Nord", "Breton" }, table.RaceNames);
        Assert.Equal(TravelsCharacterTable.SkillCount, table.SkillNames.Length);
        Assert.Equal(TravelsCharacterTable.SkillCount, table.GoverningAttributes.Length);
        Assert.Equal(26, table.GoverningAttributes[13]);

        Assert.Equal(2, table.Classes.Length);
        var second = table.Classes[1];
        Assert.Equal(1, second.Index);
        Assert.Equal("Class1", second.Name);
        Assert.Equal(1, second.DefaultRaceIndex);
        Assert.Equal(new short[] { 30, 40, 50, 60, 70, 80, 90, 100 }, second.AttributeBases);
        Assert.Equal(4, second.MagickaMultiplier);
        Assert.Equal(11, second.Field11);
        Assert.Equal(12, second.Field12);
        Assert.Equal(TravelsCharacterTable.SkillCount, second.Skills.Length);
        Assert.Equal(new TravelsClassSkill(3, 38), second.Skills[3]);
    }

    [Fact]
    public void CharacterTable_SkillCountOtherThan14_Throws()
    {
        var buf = CharacterHeader(1, 13);
        CharacterRow(buf, 0);

        var ex = Assert.Throws<InvalidDataException>(() => TravelsCharacterTable.Parse(buf.Done(), Name));
        Assert.Contains("13 names", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exactly 14", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterTable_TrailingBytes_Throw()
    {
        var buf = CharacterHeader(1);
        CharacterRow(buf, 0);
        buf.Raw(0x00, 0x00);

        var ex = Assert.Throws<InvalidDataException>(() => TravelsCharacterTable.Parse(buf.Done(), Name));
        Assert.Contains("trailing", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- itemsin.dat

    private static byte[] ItemBytes(sbyte[] types, sbyte[] slots)
    {
        var count = types.Length;
        var buf = new Buf()
            .List16("Axe", "Helmet")
            .List16(Enumerable.Range(0, count).Select(i => $"Item{i}").ToArray());

        foreach (var type in types)
        {
            buf.I8(type);
        }

        for (var i = 0; i < count; i++)
        {
            buf.I8(i + 1);
        }

        for (var i = 0; i < count; i++)
        {
            buf.I8(200 + i);
        }

        for (var i = 0; i < count; i++)
        {
            buf.I16(100 * (i + 1));
        }

        for (var i = 0; i < count; i++)
        {
            buf.I16(35 * (i + 1));
        }

        foreach (var slot in slots)
        {
            buf.I8(slot);
        }

        return buf.Done();
    }

    [Fact]
    public void ItemTable_ReadsSixColumnMajorArraysAndResolvesTypeNames()
    {
        var table = TravelsItemTable.Parse(ItemBytes(new sbyte[] { 1, 2, 1 }, new sbyte[] { 0, 4, -1 }), Name);

        Assert.Equal(new[] { "Axe", "Helmet" }, table.TypeNames);
        Assert.Equal(3, table.Items.Length);

        var second = table.Items[1];
        Assert.Equal(2, second.Id);
        Assert.Equal("Item1", second.Name);
        Assert.Equal(2, second.TypeIndex);
        Assert.Equal("Helmet", second.TypeName);
        Assert.Equal(2, second.Tier);
        Assert.Equal(201, second.Power);
        Assert.Equal(200, second.Value);
        Assert.Equal(70, second.Value35);
        Assert.Equal(4, second.Slot);

        // Power is read UNSIGNED: 200..202 would come back negative under a signed reading.
        Assert.Equal(new[] { 200, 201, 202 }, table.Items.Select(i => (int)i.Power));
    }

    [Fact]
    public void ItemTable_TypeIndexOutsideTheTypeList_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            TravelsItemTable.Parse(ItemBytes(new sbyte[] { 1, 3 }, new sbyte[] { 0, 0 }), Name));

        Assert.Contains("type index 3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("outside 1..2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemTable_SlotOutsideTheEquipmentRange_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            TravelsItemTable.Parse(ItemBytes(new sbyte[] { 1, 1 }, new sbyte[] { 0, 7 }), Name));

        Assert.Contains("equipment slot 7", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- spellsin.dat

    [Fact]
    public void SpellTable_ReadsColumnMajorColumnsThenDescriptions()
    {
        var buf = new Buf().List16("Frenzy", "Shield");
        int[][] columns =
        [
            new[] { 1, 3 },
            new[] { 10, 15 },
            new[] { 30, -2 },
            new[] { 1, 2 },
            new[] { 35, 0 },
            new[] { 3, 4 }
        ];
        foreach (var column in columns)
        {
            foreach (var value in column)
            {
                buf.I8(value);
            }
        }

        buf.Utf("Rage").Utf("Ward");

        var table = TravelsSpellTable.Parse(buf.Done(), Name);

        Assert.Equal(2, table.Spells.Length);
        var first = table.Spells[0];
        Assert.Equal(1, first.Id);
        Assert.Equal("Frenzy", first.Name);
        Assert.Equal(1, first.SchoolSkill);
        Assert.Equal(10, first.Cost);
        Assert.Equal(30, first.Duration);
        Assert.Equal(1, first.Target);
        Assert.Equal(35, first.BaseChance);
        Assert.Equal(3, first.RankRequired);
        Assert.Equal("Rage", first.Description);

        // Column-major, so spell 2's cost is the SECOND byte of the cost column, not the byte
        // after spell 1's own values.
        Assert.Equal(15, table.Spells[1].Cost);
        Assert.Equal(-2, table.Spells[1].Duration);
        Assert.Equal("Ward", table.Spells[1].Description);
    }

    [Fact]
    public void SpellTable_MissingDescription_Throws()
    {
        var buf = new Buf().List16("Frenzy");
        for (var c = 0; c < TravelsSpellTable.ColumnCount; c++)
        {
            buf.I8(1);
        }

        var ex = Assert.Throws<InvalidDataException>(() => TravelsSpellTable.Parse(buf.Done(), Name));
        Assert.Contains(Name, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- monstersin.dat

    private static Buf MonsterBytes(int count, int statSeed = 0)
    {
        var buf = new Buf().I32(count);
        for (var i = 0; i < count; i++)
        {
            buf.Utf($"Monster{i}");
        }

        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < TravelsMonsterTable.StatColumnCount; c++)
            {
                buf.I8(c == 0 ? i + 1 : statSeed + c);
            }
        }

        return buf;
    }

    [Fact]
    public void MonsterTable_ReadsAWideCountAndSeventeenUnsignedStatsPerRow()
    {
        var table = TravelsMonsterTable.Parse(MonsterBytes(2, 190).Done(), Name);

        Assert.Equal(2, table.Monsters.Length);
        var second = table.Monsters[1];
        Assert.Equal(2, second.Id);
        Assert.Equal("Monster1", second.Name);
        Assert.Equal(TravelsMonsterTable.StatColumnCount, second.Stats.Length);

        // Read unsigned: 190 + 14 = 204 would be -52 as a signed byte.
        Assert.Equal(191, second.Family);
        Assert.Equal(204, second.HitPoints);
        Assert.Equal(205, second.DropChance);
        Assert.Equal(206, second.LootRolls);
    }

    [Fact]
    public void MonsterTable_NegativeCount_Throws()
    {
        var bytes = new Buf().I32(-3).Done();
        var ex = Assert.Throws<InvalidDataException>(() => TravelsMonsterTable.Parse(bytes, Name));
        Assert.Contains("negative", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MonsterTable_ShortStatRow_Throws()
    {
        var bytes = MonsterBytes(1).Done();
        Assert.Throws<InvalidDataException>(() => TravelsMonsterTable.Parse(bytes.AsSpan(0, bytes.Length - 1), Name));
    }

    // ---------------------------------------------------------------- monsterfilenamesin.dat

    private static byte[] SpriteBytes(int stringCount)
    {
        var buf = new Buf();
        for (var i = 0; i < stringCount; i++)
        {
            // Two slots per family stay empty, as the retail files' unused parts do.
            buf.Utf(i % 7 >= 5 ? string.Empty : $"/part{i}.png");
        }

        return buf.Done();
    }

    [Fact]
    public void MonsterSpriteTable_ReadsAFixedFiveBySevenGridWithNoCount()
    {
        var table = TravelsMonsterSpriteTable.Parse(SpriteBytes(TravelsMonsterSpriteTable.EntryCount), Name);

        Assert.Equal(TravelsMonsterSpriteTable.FamilyCount, table.Families.Length);
        Assert.All(table.Families, family => Assert.Equal(TravelsMonsterSpriteTable.PartCount, family.Length));
        Assert.Equal("/part7.png", table.Families[1][0]);
        Assert.Equal(string.Empty, table.Families[1][5]);
        Assert.Equal(10, table.Families.Sum(f => f.Count(s => s.Length == 0)));
    }

    [Fact]
    public void MonsterSpriteTable_FewerThan35Strings_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            TravelsMonsterSpriteTable.Parse(SpriteBytes(TravelsMonsterSpriteTable.EntryCount - 1), Name));
        Assert.Contains(Name, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MonsterSpriteTable_MoreThan35Strings_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            TravelsMonsterSpriteTable.Parse(SpriteBytes(TravelsMonsterSpriteTable.EntryCount + 1), Name));
        Assert.Contains("trailing", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- droppeditemsin.dat

    [Fact]
    public void DropTable_ReadsItsOwnDimensionsRowMajor()
    {
        var buf = new Buf().I16(2).I16(3);
        for (var value = 1; value <= 6; value++)
        {
            buf.I8(value * 40);
        }

        var table = TravelsDropTable.Parse(buf.Done(), Name);

        Assert.Equal(2, table.RowCount);
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal(new[] { 40, 80, 120 }, table.Rows[0].Select(b => (int)b));
        Assert.Equal(new[] { 160, 200, 240 }, table.Rows[1].Select(b => (int)b));
    }

    [Fact]
    public void DropTable_NegativeDimension_Throws()
    {
        var bytes = new Buf().I16(-1).I16(5).Done();
        var ex = Assert.Throws<InvalidDataException>(() => TravelsDropTable.Parse(bytes, Name));
        Assert.Contains("negative dimension", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DropTable_GridThatDoesNotTile_Throws()
    {
        var buf = new Buf().I16(2).I16(3).I8(1).I8(2).I8(3).I8(4).I8(5).I8(6).I8(7);
        Assert.Throws<InvalidDataException>(() => TravelsDropTable.Parse(buf.Done(), Name));
    }

    // ---------------------------------------------------------------- geomin.dat

    private static byte[] GeometryBytes(int rows)
    {
        var buf = new Buf();
        for (var r = 0; r < rows; r++)
        {
            buf.I8(r == 0 ? 2 : -1).I8(11).I8(20).I8(29).I8(-1).I8(r == 0 ? -1 : 3);
        }

        return buf.Done();
    }

    [Fact]
    public void GeometryTable_ReadsThirtySevenHeaderlessRows()
    {
        var table = TravelsGeometryTable.Parse(GeometryBytes(TravelsGeometryTable.RowCount), Name);

        Assert.Equal(TravelsGeometryTable.RowCount, table.Dungeons.Length);
        var camp = table.Dungeons[0];
        Assert.Equal(1, camp.Id);
        Assert.Equal(2, camp.North);
        Assert.Equal(11, camp.East);
        Assert.Equal(20, camp.South);
        Assert.Equal(29, camp.West);
        Assert.Equal(-1, camp.ExitDirection);
        Assert.Equal(-1, camp.ReturnDirection);
        Assert.Equal(3, table.Dungeons[1].ReturnDirection);
        Assert.Equal(37, table.Dungeons[^1].Id);
    }

    [Fact]
    public void GeometryTable_WrongLength_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            TravelsGeometryTable.Parse(GeometryBytes(TravelsGeometryTable.RowCount - 1), Name));

        Assert.Contains("216 bytes", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- dungnamesin.dat

    private static byte[] DungeonNameBytes(int pairs)
    {
        var buf = new Buf();
        for (var i = 0; i < pairs; i++)
        {
            buf.Utf($"Name{i}").Utf($"Line{i}");
        }

        return buf.Done();
    }

    [Fact]
    public void DungeonNameTable_ReadsThirtySevenHeaderlessPairs()
    {
        var table = TravelsDungeonNameTable.Parse(DungeonNameBytes(TravelsDungeonNameTable.PairCount), Name);

        Assert.Equal(37, table.Names.Length);
        Assert.Equal(new TravelsDungeonName(1, "Name0", "Line0"), table.Names[0]);
        Assert.Equal(37, table.Names[^1].Id);
    }

    [Fact]
    public void DungeonNameTable_WrongPairCount_Throws()
    {
        Assert.Throws<InvalidDataException>(() =>
            TravelsDungeonNameTable.Parse(DungeonNameBytes(TravelsDungeonNameTable.PairCount - 1), Name));
    }

    // ---------------------------------------------------------------- helptext.dat

    [Fact]
    public void HelpTextTable_ReadsAWideCountThenThatManyLines()
    {
        var bytes = new Buf().I32(2).Utf("Goal").Utf("Escape the prison.").Done();
        var table = TravelsHelpTextTable.Parse(bytes, Name);

        Assert.Equal(new[] { "Goal", "Escape the prison." }, table.Lines);
    }

    [Fact]
    public void HelpTextTable_NegativeCount_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TravelsHelpTextTable.Parse(new Buf().I32(-1).Done(), Name));

        Assert.Contains("negative", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- npcstrings.dat

    [Fact]
    public void NpcStringTable_ReadsGroupsUntilEof()
    {
        var bytes = new Buf()
            .I32(2).Utf("Hello.").Utf("Goodbye.")
            .I32(1).Utf("Your inventory is full.")
            .Done();

        var table = TravelsNpcStringTable.Parse(bytes, Name);

        Assert.Equal(2, table.Groups.Length);
        Assert.Equal(3, table.TotalCount);
        Assert.Equal("Goodbye.", table.Groups[0][1]);
        Assert.Equal("Your inventory is full.", table.Groups[1][0]);
    }

    [Fact]
    public void NpcStringTable_GroupRunningPastEof_Throws()
    {
        var bytes = new Buf().I32(3).Utf("Hello.").Done();
        var ex = Assert.Throws<InvalidDataException>(() => TravelsNpcStringTable.Parse(bytes, Name));
        Assert.Contains(Name, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NpcStringTable_NegativeGroupCount_Throws()
    {
        var bytes = new Buf().I32(1).Utf("Hello.").I32(-2).Done();
        var ex = Assert.Throws<InvalidDataException>(() => TravelsNpcStringTable.Parse(bytes, Name));
        Assert.Contains("negative", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Big-endian byte builder with the Java stream's primitives.</summary>
    private sealed class Buf
    {
        private readonly List<byte> _bytes = [];

        public Buf Raw(params byte[] values)
        {
            _bytes.AddRange(values);
            return this;
        }

        public Buf I8(int value)
        {
            _bytes.Add((byte)value);
            return this;
        }

        public Buf U16(int value)
        {
            var scratch = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(scratch, (ushort)value);
            _bytes.AddRange(scratch);
            return this;
        }

        public Buf I16(int value)
        {
            return U16(value);
        }

        public Buf I32(int value)
        {
            var scratch = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(scratch, value);
            _bytes.AddRange(scratch);
            return this;
        }

        public Buf Utf(string value)
        {
            var raw = Encoding.UTF8.GetBytes(value);
            U16(raw.Length);
            _bytes.AddRange(raw);
            return this;
        }

        public Buf List16(params string[] values)
        {
            I16(values.Length);
            foreach (var value in values)
            {
                Utf(value);
            }

            return this;
        }

        public byte[] Done()
        {
            return [.. _bytes];
        }
    }
}

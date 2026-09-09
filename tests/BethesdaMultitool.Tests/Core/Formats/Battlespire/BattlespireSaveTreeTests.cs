using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     Vectors for <see cref="BattlespireSaveTree" />, shaped after the SAVE0 fixture measured
///     2026-09-07: the record envelope, the 65-byte header at UESP's offsets, the length-driven walk
///     that survives a type-0 record, the typed body views and the raw trailer.
/// </summary>
public sealed class BattlespireSaveTreeTests
{
    private const int Header = BattlespireSaveRecord.HeaderLength;

    /// <summary>
    ///     The 65 header bytes of the fixture's Player record, verbatim (file offset 0x4A). The
    ///     expected decode below was produced by an independent Python <c>struct</c> walk, so the
    ///     C# reader is checked against a reading it did not make.
    /// </summary>
    private static readonly byte[] FixturePlayerHeader =
    [
        0x54, 0x03, 0x00, 0x00, 0x03, 0x08, 0x00, 0x3B, 0x07, 0x00, 0x00, 0x5B, 0xEE, 0xC3, 0xC4, 0x00,
        0x00, 0xBC, 0xC2, 0x18, 0x1F, 0xD7, 0x43, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x50, 0xC3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0xBE, 0x74, 0xC0, 0x01, 0x8A, 0x71, 0xC0, 0x01, 0x00, 0x00, 0x00,
        0x00
    ];

    private static byte[] Record(BattlespireSaveRecordType type, int total, uint recordId, uint parentId,
        ushort pitch = 0, ushort yaw = 0, Action<byte[]>? body = null)
    {
        var r = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(r, (uint)(total - 4));
        r[4] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(5), pitch);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(7), yaw);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(33), recordId);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(61), parentId);
        body?.Invoke(r);
        return r;
    }

    private static byte[] Tree(IEnumerable<byte[]> records, int trailerLength = 0,
        uint version = BattlespireSaveTree.ExpectedVersion)
    {
        var list = records.ToList();
        var file = new byte[4 + list.Sum(r => r.Length) + trailerLength];
        BinaryPrimitives.WriteUInt32LittleEndian(file, version);
        var at = 4;
        foreach (var r in list)
        {
            r.CopyTo(file, at);
            at += r.Length;
        }

        return file;
    }

    private static void Ascii(byte[] r, int offset, string text)
    {
        Encoding.ASCII.GetBytes(text).CopyTo(r, offset);
    }

    private static byte[] Player(uint recordId = BattlespireSaveRecord.PlayerRecordId)
    {
        return Record(BattlespireSaveRecordType.Player, 856, recordId, 0, body: r =>
        {
            Ascii(r, 65, "Biggus Dickus");
            uint[] attributes = [67, 50, 35, 40, 52, 55, 50, 75];
            for (var i = 0; i < 8; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(97 + i * 4), attributes[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(129 + i * 4), attributes[i]);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(161), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(163), 50);
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(167), 6);
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(171), 70);
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(175), 70);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(179 + 16 * 12), 36); // Hand to Hand value
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(179 + 16 * 12 + 4), 28); // ...use count
            Ascii(r, 443, "Monk");
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(475), 256); // IncreaseMagery 1x
            Ascii(r, 499, "Monk");
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(559), -2);
            r[575] = 21; // F1 = Cure Health, stored list + 1
            r[576] = 64; // F2 = the first hotkey item
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(587), 30);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(615), 0xC44);
            r[639] = 3;
            r[640] = 7;
            r[641] = 4;
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(667), 15);
            r[670] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(736), 1);

            // ⚠ The fixture's PLAYER carries these bytes at +800..+807 — +803 is a MONSTER field,
            // and read as a SAVEVARID this word means the impossible ordinal 3,976,719,615.
            byte[] playerTail = [0xFF, 0x80, 0x00, 0x00, 0xED, 0x07, 0xED, 0x07];
            playerTail.CopyTo(r, 800);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(808), 0x12345);
        });
    }

    private static byte[] Item(uint recordId, uint parentId, string name, ushort itemId, uint weightHalves = 0,
        uint enchantmentId = 0)
    {
        return Record(BattlespireSaveRecordType.Item, 820, recordId, parentId, body: r =>
        {
            Ascii(r, 65, name);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(97), itemId);
            Ascii(r, 99, "MSHRT206");
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(118), weightHalves);
            r[122] = 40;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(127), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(131), itemId == 16 ? (ushort)1 : (ushort)0);
            BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(169), 1.5f);
            Ascii(r, 244, "Doht Sigil of Entry");
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(787), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(803), enchantmentId);
        });
    }

    private static byte[] Spell(BattlespireSaveRecordType type, uint recordId, string name, byte storedId, byte icon)
    {
        return Record(type, 184, recordId, BattlespireSaveRecord.PlayerRecordId, body: r =>
        {
            Ascii(r, 65, name);
            r[97] = storedId;
            r[99] = icon;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(144), BattlespireSaveSpell.TargetSelf);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(160), 12);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(168), 1);
        });
    }

    private static byte[] Automap(int width, int height, int visited)
    {
        return Record(BattlespireSaveRecordType.Automap, Header + width * height + 5, 0x13F00, 0,
            (ushort)width, (ushort)height, r =>
            {
                for (var i = 0; i < visited; i++)
                {
                    r[Header + i * 7 % (width * height)] = 1;
                }
            });
    }

    private static byte[] FullTree(int trailerLength = 187)
    {
        var file = Tree(
        [
            Player(),
            Item(0x12700, BattlespireSaveRecord.PlayerRecordId, "Shirt\" ", 19, 6),
            Item(0x0100, BattlespireSaveRecord.MapParentId, "Sack", 16),
            Record(BattlespireSaveRecordType.Object, 176, 0x33, BattlespireSaveRecord.MapParentId, body: r =>
                BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(101), -1122f)),
            Spell(BattlespireSaveRecordType.Spell, 0x1274C, "Cure Health", 21, 20),
            Spell(BattlespireSaveRecordType.Tombstone, 0x13FD6, "Cure Health", 21, 20),
            Record(BattlespireSaveRecordType.Monster, 856, 0x241, BattlespireSaveRecord.MapParentId, body: r =>
            {
                Ascii(r, 65, "Dremora");
                BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(655), 2);
                BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(803), 1);
            }),
            Record(BattlespireSaveRecordType.Options, 70, 0x13F01, 0),
            Automap(40, 30, 45)
        ], trailerLength);

        if (trailerLength > 10)
        {
            file[file.Length - trailerLength + 10] = 2;
        }

        return file;
    }

    [Fact]
    public void Parse_WalksByTheLengthWordAndHandsBackTheTrailer()
    {
        var file = FullTree();
        var tree = BattlespireSaveTree.Parse(file, "SAVETREE.DAT");

        Assert.Equal(BattlespireSaveTree.ExpectedVersion, tree.Version);
        Assert.Equal(9, tree.Records.Count);
        Assert.Equal(file.Length - 187, tree.TrailerOffset);
        Assert.Equal(187, tree.Trailer.Length);
        Assert.Equal(2, tree.Trailer.Span[10]);
        Assert.Empty(tree.SizeMismatches());

        // The type-0 record is walked like any other: only its length word sizes it.
        Assert.Equal<(byte, int)[]>(
            [(0, 1), (2, 2), (3, 1), (6, 1), (9, 1), (18, 1), (23, 1), (51, 1)],
            [.. tree.Census().Select(kv => (kv.Key, kv.Value))]);
        Assert.Equal(
            [856, 820, 820, 176, 184, 184, 856, 70, 1270],
            tree.Records.Select(r => r.TotalLength).ToArray());
        Assert.Equal(4, tree.Records[0].Offset);
        Assert.Equal(4 + 856, tree.Records[1].Offset);
    }

    [Fact]
    public void Parse_WithNoTrailer_EndsAtTheFileLength()
    {
        var file = FullTree(0);
        var tree = BattlespireSaveTree.Parse(file, "SAVETREE.DAT");

        Assert.Equal(file.Length, tree.TrailerOffset);
        Assert.Equal(0, tree.Trailer.Length);
    }

    [Fact]
    public void Parse_DecodesTheHeaderAtUespOffsets()
    {
        var record = new byte[856];
        FixturePlayerHeader.CopyTo(record, 0);
        var tree = BattlespireSaveTree.Parse(Tree([record]), "SAVETREE.DAT");
        var header = Assert.Single(tree.Records);

        Assert.Equal(852u, header.DeclaredLength);
        Assert.Equal(BattlespireSaveRecordType.Player, header.Type);
        Assert.True(header.HasTableLength);
        Assert.Equal((8, 1851, 0), (header.Pitch, header.Yaw, header.Roll));
        Assert.Equal(-1567.4486f, header.X, 0.0001f);
        Assert.Equal(-94.0f, header.Y);
        Assert.Equal(430.2429f, header.Z, 0.0001f);
        Assert.Equal(1, header.Flags);
        Assert.Equal(0u, header.FileId);
        Assert.Equal(BattlespireSaveRecord.PlayerRecordId, header.RecordId);
        Assert.Equal((-1, -1), (header.LinkId, header.Unknown2B));
        Assert.Equal((0x01C074BEu, 0x01C0718Au), (header.Unknown35, header.Unknown39));
        Assert.Equal(0u, header.ParentId);
        Assert.False(header.HasLevelId);
    }

    [Fact]
    public void Parse_RejectsARecordThatOverrunsTheFile()
    {
        var file = Tree([Player()]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 900);

        var error = Assert.Throws<InvalidDataException>(() => BattlespireSaveTree.Parse(file, "BAD.DAT"));
        Assert.Contains("past", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsARecordShorterThanTheHeader()
    {
        var file = Tree([Player()]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 10);

        var error = Assert.Throws<InvalidDataException>(() => BattlespireSaveTree.Parse(file, "BAD.DAT"));
        Assert.Contains("header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsATypedRecordOfTheWrongSizeWithoutFailing()
    {
        // ⚠ UESP suspects the Automap varies on interior levels, so a size that differs from the
        // table is REPORTED, never fatal: the walk trusts the length word alone.
        var odd = Record(BattlespireSaveRecordType.Item, 800, 0x13F02, 1);
        var tree = BattlespireSaveTree.Parse(Tree([Player(), odd]), "SAVETREE.DAT");

        var mismatch = Assert.Single(tree.SizeMismatches());
        Assert.Equal(800, mismatch.TotalLength);
        Assert.Equal(820, mismatch.TableLength);
        Assert.Null(mismatch.AsItem());
    }

    [Fact]
    public void TypedViews_DispatchOnTypeForTypedRecordsAndOnSizeForATombstone()
    {
        var tree = BattlespireSaveTree.Parse(FullTree(), "SAVETREE.DAT");
        var live = tree.FindRecord(0x1274C)!;
        var tombstone = tree.FindRecord(0x13FD6)!;

        Assert.Equal(BattlespireSaveRecordType.Tombstone, tombstone.Type);
        Assert.False(tombstone.IsKnownType);
        var spell = live.AsSpell()!;
        Assert.Equal("Cure Health", spell.Name);
        Assert.Equal((21, 20), (spell.StoredSpellId, spell.Icon));
        Assert.Equal(20, spell.SpellListIndex);
        Assert.Equal("Cure Health", spell.ListName);
        Assert.Equal(BattlespireSaveSpell.TargetSelf, spell.LastCastTarget);
        Assert.Equal(12u, spell.SpellPointCostBase);
        Assert.Equal("Restoration", spell.SchoolName);

        Assert.NotNull(tombstone.AsSpell());
        Assert.Equal("Cure Health", tombstone.AsSpell()!.Name);
        Assert.Null(tombstone.AsItem());
        Assert.Null(tombstone.AsCharacter());
        Assert.Null(live.AsItem());
        Assert.Null(live.AsCharacter());
    }

    [Fact]
    public void AsCharacter_ReadsNameAttributesClassRaceAndHotkeys()
    {
        var tree = BattlespireSaveTree.Parse(FullTree(), "SAVETREE.DAT");
        var player = tree.Player!.AsCharacter()!;

        Assert.Equal("Biggus Dickus", player.Name);
        Assert.Equal<uint[]>([67, 50, 35, 40, 52, 55, 50, 75], [.. player.Attributes]);
        Assert.Equal(player.Attributes, player.BaseAttributes);
        Assert.Equal((2, 50), (player.SpellPoints, player.SpellPointsMax));
        Assert.Equal((6, 70, 70), (player.Wounds, player.WoundsMax, player.WoundsBase));
        Assert.Equal(new BattlespireSaveSkill(36, 28, 0), player.Skills[16]);
        Assert.Equal("Hand to Hand", BattlespireSaveLists.SkillNames[16]);
        Assert.Equal(("Monk", "Monk"), (player.ClassName, player.BaseClassName));
        Assert.Equal(256u, player.Advantages.IncreaseMagery);
        Assert.Equal(-2, player.CombatState);
        Assert.Equal(21, player.Hotkeys[0]);
        Assert.Equal("Cure Health", BattlespireSaveLists.StoredSpellName(player.Hotkeys[0]));
        Assert.Equal(64, player.Hotkeys[1]);
        Assert.Equal(0x12345u, player.HotkeyItemRecordIds[0]);
        Assert.Equal(30u, player.AvailableSkillPoints);
        Assert.Equal(0xC44u, player.Flags);
        Assert.Equal((3, 7, 4), (player.Hair, player.Eyes, player.Mouth));
        Assert.Equal(15, player.EquippedWeaponItemId);
        Assert.Equal(1, player.Race);
        Assert.Equal("Breton", player.RaceName);
        Assert.Equal(1u, player.Level);
        Assert.False(player.IsDead);

        // ⚠⚠ +803 is a MONSTER field: the player's word is handed back raw and decodes to NOTHING.
        Assert.Equal(BattlespireSaveCharacterKind.Player, player.Kind);
        Assert.Equal(0xED07ED00u, player.SaveVarWord);
        Assert.Null(player.SaveVarId);
        Assert.Null(player.StaticEnemyOrdinal);

        // ⚠ On a MONSTER it is the StaticEnemy ordinal PLUS ONE (56/56 on the fixture).
        var monster = tree.FindRecord(0x241)!.AsCharacter()!;
        Assert.Equal(("Dremora", 2u, "Dremora"), (monster.Name, monster.EnemyType, monster.EnemyTypeName));
        Assert.Equal(BattlespireSaveCharacterKind.Monster, monster.Kind);
        Assert.Equal(1u, monster.SaveVarId);
        Assert.Equal(0, monster.StaticEnemyOrdinal);
        Assert.True(monster.Advantages.Equals(monster.BaseAdvantages));
    }

    [Fact]
    public void AsItem_TrimsTheBsiSuffixHalvesTheWeightAndNamesTheType()
    {
        var tree = BattlespireSaveTree.Parse(FullTree(), "SAVETREE.DAT");
        var shirt = tree.FindRecord(0x12700)!.AsItem()!;

        // ⚠ Retail stores 'Shirt" ' for BSI-named clothing; the type is Clothes (19) and the
        // stored name is the sub-type.
        Assert.Equal("Shirt\" ", shirt.RawName);
        Assert.Equal("Shirt", shirt.Name);
        Assert.Equal(19, shirt.ItemId);
        Assert.Equal("Clothes", shirt.ItemTypeName);
        Assert.Equal("MSHRT206", shirt.FileName);
        Assert.Equal(3.0, shirt.Weight);
        Assert.Equal(6u, shirt.WeightHalves);
        Assert.Equal(3u, shirt.Quantity);
        Assert.False(shirt.IsContainer);
        Assert.Equal(1.5f, shirt.ItemPosition.X);
        Assert.Equal("Doht Sigil of Entry", shirt.EnchantmentName);
        Assert.False(shirt.IsEnchanted);
        Assert.Equal(1u, shirt.Charges);
        Assert.Equal(422, shirt.EmbeddedCharacterBlock.Length);

        // 40 is Restore Spell Points only under the list + 1 convention (0-based it is unmapped).
        Assert.Equal(40, shirt.StoredSpellId);
        Assert.Equal("Restore Spell Points", shirt.SpellName);

        var sack = tree.FindRecord(0x0100)!.AsItem()!;
        Assert.Equal(("Sack", "Sack", true), (sack.Name, sack.ItemTypeName, sack.IsContainer));
    }

    [Fact]
    public void AsObject_ReadsTheBodysPoseCopyInRawUnits()
    {
        var tree = BattlespireSaveTree.Parse(FullTree(), "SAVETREE.DAT");
        var door = tree.FindRecord(0x33)!;

        Assert.True(door.HasLevelId);
        Assert.Equal(BattlespireSaveRecord.MapParentId, door.ParentId);
        Assert.Equal(-1122f, door.AsObject()!.Position.X);
    }

    [Fact]
    public void AsAutomap_SizesTheGridFromTheHeadersPitchAndYaw()
    {
        var tree = BattlespireSaveTree.Parse(FullTree(), "SAVETREE.DAT");
        var record = tree.Records.Single(r => r.Type == BattlespireSaveRecordType.Automap);
        var automap = record.AsAutomap()!;

        Assert.Equal((40, 30), (automap.Width, automap.Height));
        Assert.Equal(1200, automap.Tiles.Count);
        Assert.Equal(45, automap.VisitedCount);
        Assert.True(automap.TilesExactly);
        Assert.Equal(1, automap.TileAt(0, 0));
        Assert.Equal((0u, 0), (automap.TailWord, automap.TailByte));

        // A body too small for its declared grid cannot be an automap of that size.
        var small = Record(BattlespireSaveRecordType.Automap, Header + 100, 0x13F03, 0, 40, 30);
        var tiny = BattlespireSaveTree.Parse(Tree([small]), "SAVETREE.DAT").Records[0];
        Assert.Throws<InvalidDataException>(() => tiny.AsAutomap());
    }

    [Fact]
    public void ChildrenOf_ReturnsTheRecordsAHolderCarries()
    {
        var tree = BattlespireSaveTree.Parse(FullTree(), "SAVETREE.DAT");

        var carried = tree.ChildrenOf(BattlespireSaveRecord.PlayerRecordId).Select(r => r.RecordId).ToArray();
        Assert.Equal([0x12700, 0x1274C, 0x13FD6], carried);
        Assert.Equal(3, tree.ChildrenOf(BattlespireSaveRecord.MapParentId).Count());
        Assert.Null(tree.FindRecord(0xDEAD));
    }

    [Fact]
    public void IsSaveTree_RequiresTheVersionWordAndAPlausibleFirstRecord()
    {
        Assert.True(BattlespireSaveTree.IsSaveTree(FullTree()));
        Assert.False(BattlespireSaveTree.IsSaveTree(Tree([Player()], version: 0x126)));

        var wrongSize = Tree([Record(BattlespireSaveRecordType.Player, 800, 1, 0)]);
        Assert.False(BattlespireSaveTree.IsSaveTree(wrongSize));

        var unknownType = Tree([Record((BattlespireSaveRecordType)99, 856, 1, 0)]);
        Assert.False(BattlespireSaveTree.IsSaveTree(unknownType));
        Assert.False(BattlespireSaveTree.IsSaveTree(new byte[10]));
    }

    [Theory]
    [InlineData(BattlespireSaveRecordType.Item, 820)]
    [InlineData(BattlespireSaveRecordType.Player, 856)]
    [InlineData(BattlespireSaveRecordType.Object, 176)]
    [InlineData(BattlespireSaveRecordType.Projectile, 66)]
    [InlineData(BattlespireSaveRecordType.Spell, 184)]
    [InlineData(BattlespireSaveRecordType.Monster, 856)]
    [InlineData(BattlespireSaveRecordType.Options, 70)]
    [InlineData(BattlespireSaveRecordType.Automap, 1270)]
    [InlineData(BattlespireSaveRecordType.Effect, 514)]
    internal void TableLength_IsUespsPerTypeSize(BattlespireSaveRecordType type, int expected)
    {
        Assert.Equal(expected, BattlespireSaveTree.TableLength(type));
    }

    [Fact]
    public void TableLength_IsNullForAnUndocumentedType()
    {
        Assert.Null(BattlespireSaveTree.TableLength(BattlespireSaveRecordType.Tombstone));
        Assert.Null(BattlespireSaveTree.TableLength((BattlespireSaveRecordType)99));
    }
}

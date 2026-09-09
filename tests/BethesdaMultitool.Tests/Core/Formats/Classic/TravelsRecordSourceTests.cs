using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Synthetic checks that the Stormhold and Dawnstar record sources turn a mounted install into
///     records: the composite <c>(tableId &lt;&lt; 16) | rowOrdinal</c> identity, the joins the
///     sources perform across tables (item type, spell school, loot cell to item name, dungeon graph
///     to dungeon name), the fallback that opens Dawnstar's <c>datfiles.lmp</c> when the mount does
///     not serve its members, and the two ways this can fail — a table that is absent (short
///     collection, no throw) and one that is malformed (loud throw).
///     <para>
///         The fixtures here are written byte by byte in the big-endian Java layout the readers
///         expect, so no real game data is involved.
///     </para>
/// </summary>
public sealed class TravelsRecordSourceTests : IDisposable
{
    private static readonly string[] AttributeLabels =
    [
        "Strength", "Strength Increases",
        "Intelligence", "Intelligence Increases",
        "Willpower", "Willpower Increases",
        "Agility", "Agility Increases",
        "Speed", "Speed Increases",
        "Endurance", "Endurance Increases",
        "Personality", "Personality Increases",
        "Luck", "Luck Increases"
    ];

    private static readonly string[] SkillNames =
    [
        "Axe", "Alteration", "Blunt Weapon", "Conjuration", "Destruction", "Heavy Armor", "Illusion",
        "Light Armor", "Long Blade", "Perception", "Restoration", "Security", "Short Blade", "Speechcraft"
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "travels-records-" + Guid.NewGuid().ToString("N"));

    public TravelsRecordSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void AStormholdInstallSynthesizesOneRecordPerTableRow()
    {
        var install = Path.Combine(_root, "stormhold");
        Directory.CreateDirectory(install);
        foreach (var (name, bytes) in SharedTables())
        {
            File.WriteAllBytes(Path.Combine(install, name), bytes);
        }

        File.WriteAllBytes(Path.Combine(install, "dungnamesin.dat"), DungeonNames());
        File.WriteAllBytes(Path.Combine(install, "npcstrings.dat"), NpcStrings());

        var records = Populate(install, StormholdRecordSource.Populate);

        // 2 classes, 2 races, 14 skills, 2 items, 2 spells, 2 monsters, 5 sprite families,
        // 2 loot rows, 37 dungeons, 5 NPC strings.
        Assert.Equal(73, records.Count);
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
        Assert.All(records, r => Assert.InRange(
            ClassicFormIdScheme.DomainOf(r.FormId),
            StormholdRecordSource.FirstDomain,
            StormholdRecordSource.LastDomain));

        // Every signature is the game's letter plus the table's code.
        Assert.All(records, r => Assert.StartsWith(
            StormholdRecordSource.SignaturePrefix, r.RecordType, StringComparison.Ordinal));
        Assert.All(records, r => Assert.Equal(4, r.RecordType.Length));

        var barbarian = Single(records, StormholdRecordSource.ClassRecordType, "CLASS_Barbarian");
        Assert.Equal("Barbarian", barbarian.FullName);

        // Class row 0 -> table 0x01, ordinal 0, in the first Stormhold domain.
        Assert.Equal(0x4001_0000u, barbarian.FormId);
        Assert.Equal("Breton", barbarian.Fields["DefaultRace"]);
        Assert.Equal(30, barbarian.Fields["Attribute_Strength"]);
        Assert.Equal(1, barbarian.Fields["KnownSkills"]);
        Assert.Equal("rank 2, 35%", barbarian.Fields["Skill_Axe"]);

        var nord = Single(records, StormholdRecordSource.RaceRecordType, "RACE_Nord");
        Assert.Equal(0x4002_0000u, nord.FormId);
        Assert.Equal("Knight", nord.Fields["DefaultForClasses"]);
        Assert.Equal(1, nord.Fields["DefaultForClassCount"]);

        var axe = Single(records, StormholdRecordSource.SkillRecordType, "SKILL_Axe");
        Assert.Equal(0x4003_0000u, axe.FormId);
        Assert.Equal(0, axe.Fields["GoverningAttributeIndex"]);
        Assert.Equal("Strength", axe.Fields["GoverningAttribute"]);
        Assert.Equal("Barbarian", axe.Fields["TaughtToClasses"]);

        var ironAxe = Single(records, StormholdRecordSource.ItemRecordType, "ITEM_Iron_Axe");
        Assert.Equal(0x4004_0000u, ironAxe.FormId);
        Assert.Equal("Iron Axe", ironAxe.FullName);
        Assert.Equal("Axe", ironAxe.Fields["Type"]);
        Assert.Equal(100, ironAxe.Fields["Value"]);
        Assert.True((bool)ironAxe.Fields["Equippable"]!);

        var spell = Single(records, StormholdRecordSource.SpellRecordType, "SPELL_Fireball");
        Assert.Equal(0x4005_0000u, spell.FormId);

        // The school column is an index into charin.dat's skill list, resolved by the source.
        Assert.Equal("Destruction", spell.Fields["School"]);
        Assert.Equal("Burns the target.", spell.Fields["Description"]);

        var rat = Single(records, StormholdRecordSource.MonsterRecordType, "MONSTER_Rat");
        Assert.Equal(0x4006_0000u, rat.FormId);
        Assert.Equal(1, rat.Fields["Family"]);
        Assert.Equal(14, rat.Fields["HitPoints"]);

        // Columns 2..13 are undecoded, so they survive verbatim rather than as invented fields.
        Assert.Equal("1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 40, 2", rat.Fields["Stats"]);

        var lootRow = Single(records, StormholdRecordSource.DropRecordType, "LOOTROW00");
        Assert.Equal(0x4007_0000u, lootRow.FormId);
        Assert.Equal("Iron Axe", lootRow.Fields["WeaponItemName"]);
        Assert.Equal("Round Shield", lootRow.Fields["ArmourItemName"]);
        Assert.Equal("Iron Axe Round Shield", lootRow.FullName);

        // Column 2 is a SPELL id, so it must not be resolved against the item table.
        Assert.Equal(1, lootRow.Fields["ScrollSpell"]);
        Assert.DoesNotContain("ScrollSpellName", lootRow.Fields.Keys, StringComparer.Ordinal);

        var camp = Single(records, StormholdRecordSource.DungeonRecordType, "DUNGEON01_Dungeon_Camp");
        Assert.Equal(0x4008_0000u, camp.FormId);
        Assert.Equal("Dungeon Camp", camp.FullName);
        Assert.True((bool)camp.Fields["IsCamp"]!);
        Assert.Equal(2, camp.Fields["North"]);
        Assert.Equal(29, camp.Fields["West"]);

        var family = Single(records, StormholdRecordSource.SpriteRecordType, "SPRITEFAMILY0_mon0_0");
        Assert.Equal(0x400A_0000u, family.FormId);
        Assert.Equal(7, family.Fields["PartsUsed"]);
        Assert.Equal("/mon0_0.cus", family.Fields["Part0"]);
        Assert.Equal(3, Single(records, StormholdRecordSource.SpriteRecordType, "SPRITEFAMILY1_mon1_0")
            .Fields["PartsUsed"]);

        // NPC strings live in the NEXT domain, keyed group << 8 | line.
        var firstLine = Single(records, StormholdRecordSource.StringRecordType, "NPCSTR_G0_L00");
        Assert.Equal(0x4100_0000u, firstLine.FormId);
        Assert.Equal("Hello there.", firstLine.FullName);
        Assert.Equal(0x4100_0102u, Single(records, StormholdRecordSource.StringRecordType, "NPCSTR_G1_L02").FormId);
    }

    [Fact]
    public void ADawnstarInstallReadsItsTablesOutOfTheLump()
    {
        var install = Path.Combine(_root, "dawnstar");
        Directory.CreateDirectory(install);

        var members = SharedTables().ToList();
        members.Add(("helptext.dat", HelpText()));
        File.WriteAllBytes(Path.Combine(install, "datfiles.lmp"), Lump(members));

        // npcstrings.dat is loose in both games — Dawnstar packs everything else and not this.
        File.WriteAllBytes(Path.Combine(install, "npcstrings.dat"), NpcStrings());

        var records = Populate(install, DawnstarRecordSource.Populate);

        // Same census as Stormhold's plus 2 help lines; the dungeons are nameless here.
        Assert.Equal(75, records.Count);
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
        Assert.All(records, r => Assert.StartsWith(
            DawnstarRecordSource.SignaturePrefix, r.RecordType, StringComparison.Ordinal));

        var barbarian = Single(records, DawnstarRecordSource.ClassRecordType, "CLASS_Barbarian");
        Assert.Equal(0x4401_0000u, barbarian.FormId);

        var help = Single(records, DawnstarRecordSource.HelpRecordType, "HELP01");
        Assert.Equal(0x4409_0001u, help.FormId);
        Assert.Equal("Kill things.", help.Fields["Text"]);

        // No dungnamesin.dat exists for Dawnstar, so its dungeons carry the graph and no name.
        var camp = Single(records, DawnstarRecordSource.DungeonRecordType, "DUNGEON01");
        Assert.Null(camp.FullName);
        Assert.DoesNotContain("NameLine1", camp.Fields.Keys, StringComparer.Ordinal);
        Assert.Equal(37, records.Count(r => r.RecordType == DawnstarRecordSource.DungeonRecordType));

        Assert.Equal(0x4500_0000u, Single(records, DawnstarRecordSource.StringRecordType, "NPCSTR_G0_L00").FormId);
    }

    [Fact]
    public void TheSameRowKeepsItsIdWhetherTheTablesAreLooseOrInALump()
    {
        var loose = Path.Combine(_root, "loose");
        var lumped = Path.Combine(_root, "lumped");
        Directory.CreateDirectory(loose);
        Directory.CreateDirectory(lumped);

        var members = SharedTables().ToList();
        foreach (var (name, bytes) in members)
        {
            File.WriteAllBytes(Path.Combine(loose, name), bytes);
        }

        File.WriteAllBytes(Path.Combine(lumped, "datfiles.lmp"), Lump(members));

        var fromLoose = Populate(loose, DawnstarRecordSource.Populate);
        var fromLump = Populate(lumped, DawnstarRecordSource.Populate);

        Assert.NotEmpty(fromLoose);
        Assert.Equal(
            fromLoose.Select(r => (r.FormId, r.RecordType, r.EditorId)),
            fromLump.Select(r => (r.FormId, r.RecordType, r.EditorId)));
    }

    [Fact]
    public void AnInstallMissingTablesYieldsAShorterCollectionRatherThanThrowing()
    {
        var install = Path.Combine(_root, "partial");
        Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, "geomin.dat"), DungeonGraph());

        var records = Populate(install, StormholdRecordSource.Populate);

        Assert.Equal(37, records.Count);
        Assert.All(records, r => Assert.Equal(StormholdRecordSource.DungeonRecordType, r.RecordType));

        // With no dungnamesin.dat the ids are unchanged and only the names are missing.
        Assert.Equal(0x4008_0000u, records[0].FormId);
        Assert.Null(records[0].FullName);
    }

    [Fact]
    public void AnEmptyInstallProducesNoRecords()
    {
        var install = Path.Combine(_root, "empty");
        Directory.CreateDirectory(install);

        Assert.Empty(Populate(install, StormholdRecordSource.Populate));
        Assert.Empty(Populate(install, DawnstarRecordSource.Populate));
    }

    [Fact]
    public void AMalformedTableThrowsNamingTheFile()
    {
        var install = Path.Combine(_root, "malformed");
        Directory.CreateDirectory(install);

        // The dungeon graph is a fixed 37 x 6 grid; one byte short is not a shorter graph.
        File.WriteAllBytes(Path.Combine(install, "geomin.dat"), DungeonGraph()[..^1]);

        var error = Assert.Throws<InvalidDataException>(() => Populate(install, StormholdRecordSource.Populate));
        Assert.Contains("geomin.dat", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnNpcStringGroupTooLargeForTheKeyIsRejected()
    {
        var install = Path.Combine(_root, "overlong");
        Directory.CreateDirectory(install);

        // group << 8 | line allows 256 lines per group; 257 would alias line 0 of the next group.
        var bytes = new List<byte>();
        WriteInt32(bytes, 257);
        for (var i = 0; i < 257; i++)
        {
            WriteUtf(bytes, "line " + i.ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllBytes(Path.Combine(install, "npcstrings.dat"), [.. bytes]);

        var error = Assert.Throws<InvalidDataException>(() => Populate(install, StormholdRecordSource.Populate));
        Assert.Contains("npcstrings.dat", error.Message, StringComparison.Ordinal);
        Assert.Contains("257", error.Message, StringComparison.Ordinal);
    }

    private static List<GenericEsmRecord> Populate(
        string directory,
        Action<IGameFileSystem, RecordCollection, CancellationToken> populate)
    {
        using var install = new LooseFileSystem(directory);
        var records = new RecordCollection();
        populate(install, records, CancellationToken.None);
        return records.GenericRecords;
    }

    private static GenericEsmRecord Single(
        IReadOnlyList<GenericEsmRecord> records,
        string recordType,
        string editorId)
    {
        return Assert.Single(records, r =>
            r.RecordType == recordType && string.Equals(r.EditorId, editorId, StringComparison.Ordinal));
    }

    /// <summary>The seven tables both games ship, in the shapes their readers require.</summary>
    private static List<(string Name, byte[] Bytes)> SharedTables()
    {
        return
        [
            ("charin.dat", CharacterTable()),
            ("droppeditemsin.dat", DropTable()),
            ("geomin.dat", DungeonGraph()),
            ("itemsin.dat", ItemTable()),
            ("monsterfilenamesin.dat", SpriteTable()),
            ("monstersin.dat", MonsterTable()),
            ("spellsin.dat", SpellTable())
        ];
    }

    private static byte[] CharacterTable()
    {
        var bytes = new List<byte>();
        WriteList(bytes, ["Level", "Health"]);
        WriteList(bytes, AttributeLabels);
        WriteList(bytes, ["Barbarian", "Knight"]);
        WriteList(bytes, ["Nord", "Breton"]);
        WriteList(bytes, SkillNames);

        // Governing attribute per skill: an EVEN index into the attribute label list.
        for (var skill = 0; skill < SkillNames.Length; skill++)
        {
            WriteInt16(bytes, 2 * (skill % 8));
        }

        WriteClassRow(bytes, 0, 1, 30, 3, 2, 0);
        WriteClassRow(bytes, 1, 0, 40, 4, 0, 3);
        return [.. bytes];
    }

    /// <summary>One 41-short class row: index, default race, 8 attributes, 3 fields, 14 skill pairs.</summary>
    private static void WriteClassRow(
        List<byte> bytes, int index, int defaultRace, int attributeBase, int magicka, int knownRank, int knownSkill)
    {
        WriteInt16(bytes, index);
        WriteInt16(bytes, defaultRace);
        for (var a = 0; a < 8; a++)
        {
            WriteInt16(bytes, attributeBase + a);
        }

        WriteInt16(bytes, magicka);
        WriteInt16(bytes, 11);
        WriteInt16(bytes, 12);

        for (var skill = 0; skill < SkillNames.Length; skill++)
        {
            WriteInt16(bytes, skill == knownSkill ? knownRank : 0);
            WriteInt16(bytes, 35 + skill);
        }
    }

    private static byte[] ItemTable()
    {
        var bytes = new List<byte>();
        WriteList(bytes, ["Axe", "Shield"]);
        WriteList(bytes, ["Iron Axe", "Round Shield"]);

        // Six COLUMN-major arrays: type, tier, power, value, value35, slot.
        bytes.AddRange([1, 2]);
        bytes.AddRange([1, 2]);
        bytes.AddRange([10, 20]);
        WriteInt16(bytes, 100);
        WriteInt16(bytes, 200);
        WriteInt16(bytes, 35);
        WriteInt16(bytes, 70);
        bytes.AddRange([0, 5]);
        return [.. bytes];
    }

    private static byte[] SpellTable()
    {
        var bytes = new List<byte>();
        WriteList(bytes, ["Fireball", "Heal"]);

        // Six column arrays: school, cost, duration, target, base chance, rank.
        bytes.AddRange([4, 10]);
        bytes.AddRange([12, 8]);
        bytes.AddRange([unchecked((byte)-1), 30]);
        bytes.AddRange([2, 1]);
        bytes.AddRange([0, 40]);
        bytes.AddRange([3, 2]);

        WriteUtf(bytes, "Burns the target.");
        WriteUtf(bytes, "Restores health.");
        return [.. bytes];
    }

    private static byte[] MonsterTable()
    {
        var bytes = new List<byte>();
        WriteInt32(bytes, 2);
        WriteUtf(bytes, "Rat");
        WriteUtf(bytes, "Wolf");

        for (var monster = 0; monster < 2; monster++)
        {
            // Column 0 is the monster's id, which is its ordinal + 1.
            bytes.Add((byte)(monster + 1));
            for (var column = 1; column < 17; column++)
            {
                bytes.Add(column switch
                {
                    1 => (byte)(monster + 1),
                    15 => 40,
                    16 => 2,
                    _ => (byte)column
                });
            }
        }

        return [.. bytes];
    }

    private static byte[] SpriteTable()
    {
        var bytes = new List<byte>();
        for (var family = 0; family < 5; family++)
        {
            for (var part = 0; part < 7; part++)
            {
                // Family 0 uses all seven slots; the others leave the tail empty, as retail does.
                var used = family == 0 || part < 3;
                WriteUtf(bytes, used
                    ? $"/mon{family}_{part}.cus"
                    : string.Empty);
            }
        }

        return [.. bytes];
    }

    private static byte[] DropTable()
    {
        var bytes = new List<byte>();
        WriteInt16(bytes, 2);
        WriteInt16(bytes, 5);

        // magic item, scroll item, scroll SPELL id, armour item, weapon item — all 1-based.
        bytes.AddRange([1, 2, 1, 2, 1]);
        bytes.AddRange([2, 1, 2, 1, 2]);
        return [.. bytes];
    }

    private static byte[] DungeonGraph()
    {
        var bytes = new List<byte>(222);

        // Row 0 is the camp: four neighbours, no onward or return door.
        bytes.AddRange([2, 11, 20, 29, unchecked((byte)-1), unchecked((byte)-1)]);
        for (var row = 1; row < 37; row++)
        {
            bytes.AddRange([1, unchecked((byte)-1), unchecked((byte)-1), unchecked((byte)-1), 1, 3]);
        }

        return [.. bytes];
    }

    private static byte[] DungeonNames()
    {
        var bytes = new List<byte>();
        WriteUtf(bytes, "Dungeon");
        WriteUtf(bytes, "Camp");
        for (var pair = 1; pair < 37; pair++)
        {
            WriteUtf(bytes, "Hall");
            WriteUtf(bytes, pair.ToString(CultureInfo.InvariantCulture));
        }

        return [.. bytes];
    }

    private static byte[] HelpText()
    {
        var bytes = new List<byte>();
        WriteInt32(bytes, 2);
        WriteUtf(bytes, "Goal");
        WriteUtf(bytes, "Kill things.");
        return [.. bytes];
    }

    private static byte[] NpcStrings()
    {
        var bytes = new List<byte>();
        WriteInt32(bytes, 2);
        WriteUtf(bytes, "Hello there.");
        WriteUtf(bytes, "Goodbye.");
        WriteInt32(bytes, 3);
        WriteUtf(bytes, "One");
        WriteUtf(bytes, "Two");
        WriteUtf(bytes, "Three");
        return [.. bytes];
    }

    /// <summary>
    ///     Packs members into a Dawnstar lump: a directory of <c>-name-</c> + u32 offset + u16
    ///     length that ends where the first payload begins, then the payloads tiling to EOF.
    /// </summary>
    private static byte[] Lump(IReadOnlyList<(string Name, byte[] Bytes)> members)
    {
        var directoryLength = members.Sum(m => m.Name.Length + 8);
        var directory = new List<byte>(directoryLength);
        var offset = directoryLength;
        foreach (var (name, bytes) in members)
        {
            directory.Add(0x2D);
            directory.AddRange(Encoding.ASCII.GetBytes(name));
            directory.Add(0x2D);
            directory.Add((byte)(offset >> 24));
            directory.Add((byte)(offset >> 16));
            directory.Add((byte)(offset >> 8));
            directory.Add((byte)offset);
            directory.Add((byte)(bytes.Length >> 8));
            directory.Add((byte)bytes.Length);
            offset += bytes.Length;
        }

        var lump = new List<byte>(offset);
        lump.AddRange(directory);
        foreach (var (_, bytes) in members)
        {
            lump.AddRange(bytes);
        }

        return [.. lump];
    }

    private static void WriteList(List<byte> bytes, string[] values)
    {
        WriteInt16(bytes, values.Length);
        foreach (var value in values)
        {
            WriteUtf(bytes, value);
        }
    }

    private static void WriteUtf(List<byte> bytes, string value)
    {
        var payload = Encoding.UTF8.GetBytes(value);
        bytes.Add((byte)(payload.Length >> 8));
        bytes.Add((byte)payload.Length);
        bytes.AddRange(payload);
    }

    private static void WriteInt16(List<byte> bytes, int value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void WriteInt32(List<byte> bytes, int value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}

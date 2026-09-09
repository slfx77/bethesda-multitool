using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Travels;
using BethesdaMultitool.Core.Formats.Travels.Dawnstar;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) that the shared Travels table readers parse the real
///     Stormhold and Dawnstar shipping data — Stormhold's loose JAR entries and Dawnstar's
///     <c>datfiles.lmp</c> members — and reproduce the census measured on 2026-09-05. These are
///     fixed fixture files, so exact counts are legitimate pins: every layout here is headerless
///     or count-driven with no magic number, so "it tiles to the last byte, with these counts" is
///     the entire correctness argument.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TravelsTableRetailTests
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static string Require(string? path, string what)
    {
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage(what));
        return path;
    }

    private static ArchiveReader OpenStormhold()
    {
        return ArchiveReader.Open(Require(RealAssetPaths.Travels.StormholdJar(), "Stormhold JAR"));
    }

    private static ArchiveReader OpenDawnstar()
    {
        return ArchiveReader.Open(Require(RealAssetPaths.Travels.DawnstarJar(), "Dawnstar JAR"));
    }

    private static byte[] JarMember(ArchiveReader reader, string name)
    {
        var bytes = reader.ReadFile(name);
        Assert.NotNull(bytes);
        return bytes;
    }

    private static DawnstarLumpArchive DataLump(ArchiveReader dawnstar)
    {
        return DawnstarLumpArchive.Parse(JarMember(dawnstar, "datfiles.lmp"), "datfiles.lmp");
    }

    private static byte[] LumpMember(DawnstarLumpArchive lump, string name)
    {
        Assert.True(lump.TryGetEntry(name, out var entry), $"{name} is missing from datfiles.lmp.");
        return lump.Read(entry).ToArray();
    }

    [Fact]
    public void DawnstarLumps_CarryTheirMeasuredDirectories()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var dawnstar = OpenDawnstar();

        var dataBytes = JarMember(dawnstar, "datfiles.lmp");
        Assert.Equal(11_217, dataBytes.Length);
        Assert.True(DawnstarLumpArchive.TryProbe(dataBytes));

        var data = DawnstarLumpArchive.Parse(dataBytes, "datfiles.lmp");
        Assert.Equal(8, data.Entries.Length);
        Assert.Equal(
            new[]
            {
                "charin.dat", "droppeditemsin.dat", "geomin.dat", "helptext.dat", "itemsin.dat",
                "monsterfilenamesin.dat", "monstersin.dat", "spellsin.dat"
            },
            data.Entries.Select(e => e.Name));
        Assert.Equal(173, data.Entries[0].Offset);

        var imageBytes = JarMember(dawnstar, "imgfiles.lmp");
        Assert.Equal(87_961, imageBytes.Length);
        Assert.True(DawnstarLumpArchive.TryProbe(imageBytes));

        var images = DawnstarLumpArchive.Parse(imageBytes, "imgfiles.lmp");
        Assert.Equal(43, images.Entries.Length);
        Assert.Equal(940, images.Entries[0].Offset);
        Assert.All(images.Entries, entry =>
        {
            Assert.EndsWith(".png", entry.Name, StringComparison.Ordinal);
            var payload = images.Read(entry).Span;
            Assert.True(
                payload.StartsWith(PngSignature),
                $"{entry.Name} is not a PNG.");
        });

        // The directory is not sorted in the image lump, so no reader may assume it is.
        Assert.False(images.Entries.Select(e => e.Name).SequenceEqual(
            images.Entries.Select(e => e.Name).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void StormholdTables_ParseWithTheMeasuredCensus()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var jar = OpenStormhold();

        var characters = TravelsCharacterTable.Parse(JarMember(jar, "charin.dat"), "charin.dat");
        AssertCharacterShape(characters);
        Assert.Equal(
            new short[] { 0, 4, 0, 2, 4, 10, 12, 6, 0, 2, 4, 2, 8, 12 },
            characters.GoverningAttributes);
        Assert.Equal("Barbarian", characters.Classes[0].Name);
        Assert.Equal(1, characters.Classes[0].DefaultRaceIndex);

        var items = TravelsItemTable.Parse(JarMember(jar, "itemsin.dat"), "itemsin.dat");
        Assert.Equal(17, items.TypeNames.Length);
        Assert.Equal(109, items.Items.Length);
        AssertSlotIsAFunctionOfType(items);

        // value35 is exactly floor(value * 7 / 20) on all 109 Stormhold rows.
        Assert.All(items.Items, item => Assert.Equal(item.Value * 7 / 20, item.Value35));

        AssertSpellShape(TravelsSpellTable.Parse(JarMember(jar, "spellsin.dat"), "spellsin.dat"));

        var monsters = TravelsMonsterTable.Parse(JarMember(jar, "monstersin.dat"), "monstersin.dat");
        Assert.Equal(41, monsters.Monsters.Length);
        AssertMonsterIdsAreOrdinals(monsters);

        var sprites = TravelsMonsterSpriteTable.Parse(
            JarMember(jar, "monsterfilenamesin.dat"), "monsterfilenamesin.dat");
        AssertSpriteGrid(sprites, 33, ".cus");

        var drops = TravelsDropTable.Parse(JarMember(jar, "droppeditemsin.dat"), "droppeditemsin.dat");
        Assert.Equal(43, drops.RowCount);
        Assert.Equal(5, drops.ColumnCount);

        var geometry = TravelsGeometryTable.Parse(JarMember(jar, "geomin.dat"), "geomin.dat");
        Assert.Equal(37, geometry.Dungeons.Length);
        Assert.Equal(new sbyte[] { 2, 11, 20, 29 }, new[]
        {
            geometry.Dungeons[0].North, geometry.Dungeons[0].East,
            geometry.Dungeons[0].South, geometry.Dungeons[0].West
        });

        var dungeonNames = TravelsDungeonNameTable.Parse(
            JarMember(jar, "dungnamesin.dat"), "dungnamesin.dat");
        Assert.Equal(37, dungeonNames.Names.Length);
        Assert.Equal(new TravelsDungeonName(1, "Dungeon", "Camp"), dungeonNames.Names[0]);

        var strings = TravelsNpcStringTable.Parse(JarMember(jar, "npcstrings.dat"), "npcstrings.dat");
        Assert.Equal(new[] { 20, 20, 20, 20, 5, 22, 5, 41 }, strings.Groups.Select(g => g.Length));
        Assert.Equal(153, strings.TotalCount);
    }

    [Fact]
    public void DawnstarTables_ParseFromTheLumpWithTheMeasuredCensus()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var jar = OpenDawnstar();
        var lump = DataLump(jar);

        var characters = TravelsCharacterTable.Parse(LumpMember(lump, "charin.dat"), "charin.dat");
        AssertCharacterShape(characters);
        Assert.Equal(
            new short[] { 0, 2, 0, 2, 4, 10, 12, 6, 0, 2, 2, 2, 8, 12 },
            characters.GoverningAttributes);

        var items = TravelsItemTable.Parse(LumpMember(lump, "itemsin.dat"), "itemsin.dat");
        Assert.Equal(15, items.TypeNames.Length);
        Assert.Equal(101, items.Items.Length);
        Assert.Contains("Magic Item", items.TypeNames);
        AssertSlotIsAFunctionOfType(items);

        // The 35 % relation holds on 100 of the 101 rows — the Troll War Axe is the exception the
        // spec measured, and pinning it keeps that from being "fixed" into a wrong invariant.
        var offBy = items.Items.Where(item => item.Value35 != item.Value * 7 / 20).ToList();
        Assert.Single(offBy);
        Assert.Equal(2209, offBy[0].Value);
        Assert.Equal(683, offBy[0].Value35);

        AssertSpellShape(TravelsSpellTable.Parse(LumpMember(lump, "spellsin.dat"), "spellsin.dat"));

        var monsters = TravelsMonsterTable.Parse(LumpMember(lump, "monstersin.dat"), "monstersin.dat");
        Assert.Equal(42, monsters.Monsters.Length);
        AssertMonsterIdsAreOrdinals(monsters);
        Assert.Equal(10, monsters.Monsters.Max(m => (int)m.Family));

        var sprites = TravelsMonsterSpriteTable.Parse(
            LumpMember(lump, "monsterfilenamesin.dat"), "monsterfilenamesin.dat");
        AssertSpriteGrid(sprites, 26, ".png");

        var drops = TravelsDropTable.Parse(LumpMember(lump, "droppeditemsin.dat"), "droppeditemsin.dat");
        Assert.Equal(43, drops.RowCount);
        Assert.Equal(5, drops.ColumnCount);

        var geometry = TravelsGeometryTable.Parse(LumpMember(lump, "geomin.dat"), "geomin.dat");
        Assert.Equal(37, geometry.Dungeons.Length);

        var help = TravelsHelpTextTable.Parse(LumpMember(lump, "helptext.dat"), "helptext.dat");
        Assert.Equal(35, help.Lines.Length);
        Assert.Equal("Goal", help.Lines[0]);
        Assert.All(help.Lines, line => Assert.NotEmpty(line));

        // npcstrings.dat is the one table Dawnstar keeps LOOSE rather than in the lump.
        var strings = TravelsNpcStringTable.Parse(JarMember(jar, "npcstrings.dat"), "npcstrings.dat");
        Assert.Equal(new[] { 3, 3, 3, 3, 14, 16, 16, 16, 16, 77 }, strings.Groups.Select(g => g.Length));
        Assert.Equal(167, strings.TotalCount);
    }

    [Fact]
    public void SharedTables_AreByteIdenticalExceptCharinsTwoSkillBytes()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var stormhold = OpenStormhold();
        using var dawnstar = OpenDawnstar();
        var lump = DataLump(dawnstar);

        foreach (var name in new[] { "spellsin.dat", "droppeditemsin.dat", "geomin.dat" })
        {
            Assert.Equal(JarMember(stormhold, name), LumpMember(lump, name));
        }

        var stormholdCharin = JarMember(stormhold, "charin.dat");
        var dawnstarCharin = LumpMember(lump, "charin.dat");
        Assert.Equal(1_260, stormholdCharin.Length);
        Assert.Equal(stormholdCharin.Length, dawnstarCharin.Length);

        var differences = Enumerable.Range(0, stormholdCharin.Length)
            .Where(i => stormholdCharin[i] != dawnstarCharin[i])
            .ToList();

        // Both differing bytes are governing-attribute entries 1 and 10 (Stormhold 4, Dawnstar 2);
        // every string and every class row is shared.
        Assert.Equal(new[] { 661, 679 }, differences);

        var stormholdTable = TravelsCharacterTable.Parse(stormholdCharin, "charin.dat");
        var dawnstarTable = TravelsCharacterTable.Parse(dawnstarCharin, "charin.dat");
        Assert.Equal(stormholdTable.ClassNames, dawnstarTable.ClassNames);
        Assert.Equal(stormholdTable.SkillNames, dawnstarTable.SkillNames);
        Assert.Equal(
            stormholdTable.Classes.Select(c => (c.Name, c.DefaultRaceIndex, c.MagickaMultiplier)),
            dawnstarTable.Classes.Select(c => (c.Name, c.DefaultRaceIndex, c.MagickaMultiplier)));
    }

    private static void AssertCharacterShape(TravelsCharacterTable table)
    {
        Assert.Equal(10, table.StatLabels.Length);
        Assert.Equal(16, table.AttributeLabels.Length);
        Assert.Equal(7, table.ClassNames.Length);
        Assert.Equal(6, table.RaceNames.Length);
        Assert.Equal(14, table.SkillNames.Length);
        Assert.Equal(7, table.Classes.Length);
        Assert.All(table.Classes, row =>
        {
            Assert.Equal(8, row.AttributeBases.Length);
            Assert.Equal(14, row.Skills.Length);
            Assert.InRange(row.DefaultRaceIndex, 0, (short)(table.RaceNames.Length - 1));
        });

        // Every governing entry is an EVEN index into the attribute list — the odd slots are the
        // "<Attr> Increases" counters, never a governing attribute.
        Assert.All(table.GoverningAttributes, value =>
        {
            Assert.InRange(value, 0, (short)(table.AttributeLabels.Length - 1));
            Assert.Equal(0, value % 2);
        });
    }

    private static void AssertSpellShape(TravelsSpellTable table)
    {
        Assert.Equal(25, table.Spells.Length);

        // Five spells per school, in school order, so spell i belongs to school [1,3,4,6,10][i/5].
        var schools = new sbyte[] { 1, 3, 4, 6, 10 };
        for (var i = 0; i < table.Spells.Length; i++)
        {
            Assert.Equal(schools[i / 5], table.Spells[i].SchoolSkill);
        }

        Assert.All(table.Spells, spell =>
        {
            Assert.InRange(spell.Cost, (sbyte)6, (sbyte)20);
            Assert.InRange(spell.Target, (sbyte)1, (sbyte)3);
            Assert.NotEmpty(spell.Description);
        });
    }

    private static void AssertMonsterIdsAreOrdinals(TravelsMonsterTable table)
    {
        for (var i = 0; i < table.Monsters.Length; i++)
        {
            Assert.Equal(i + 1, table.Monsters[i].Id);
            Assert.NotEmpty(table.Monsters[i].Name);
        }
    }

    private static void AssertSlotIsAFunctionOfType(TravelsItemTable table)
    {
        var slots = new Dictionary<int, sbyte>();
        foreach (var item in table.Items)
        {
            if (slots.TryGetValue(item.TypeIndex, out var slot))
            {
                Assert.Equal(slot, item.Slot);
            }
            else
            {
                slots[item.TypeIndex] = item.Slot;
            }
        }

        Assert.Equal(table.TypeNames.Length, slots.Count);
    }

    private static void AssertSpriteGrid(TravelsMonsterSpriteTable table, int nonEmpty, string extension)
    {
        Assert.Equal(5, table.Families.Length);
        Assert.All(table.Families, family => Assert.Equal(7, family.Length));

        var names = table.Families.SelectMany(f => f).ToList();
        Assert.Equal(35, names.Count);
        Assert.Equal(nonEmpty, names.Count(n => n.Length > 0));
        Assert.All(names.Where(n => n.Length > 0), name =>
        {
            Assert.StartsWith("/", name, StringComparison.Ordinal);
            Assert.EndsWith(extension, name, StringComparison.Ordinal);
        });
    }
}
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Classic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Record synthesis for the Arena vertical: the .INF and TEMPLATE.DAT documents become
///     <c>AINF</c>/<c>ATPL</c> generic records with <see cref="ClassicFormIdScheme" /> ids. The
///     ids must be derived from source identity, never enumeration order, so a <c>diff</c>
///     between two installs compares like with like.
/// </summary>
public class ArenaRecordSourceTests
{
    private static byte[] Inf(string text)
    {
        return Encoding.Latin1.GetBytes(text);
    }

    [Fact]
    public void BuildInfRecord_CarriesSectionCountsAndIdentity()
    {
        var record = ArenaRecordSource.BuildInfRecord(
            "AGTEMPL.INF",
            Inf("@FLOORS\nfloora.set #4\n@WALLS\nwall.img\n@FLATS\nking.img\n@SOUND\ndoor.voc 12\n"));

        Assert.Equal("AINF", record.RecordType);
        Assert.Equal("AGTEMPL", record.EditorId);
        Assert.Equal(ArenaRecordSource.InfDomain, ClassicFormIdScheme.DomainOf(record.FormId));
        Assert.Equal(1, record.Fields["FloorTextures"]);
        Assert.Equal(1, record.Fields["WallTextures"]);
        Assert.Equal(1, record.Fields["Flats"]);
        Assert.Equal(1, record.Fields["Sounds"]);
        Assert.Equal("12=DOOR.VOC", record.Fields["SoundFiles"]);
    }

    [Fact]
    public void BuildInfRecord_SurfacesLoreTextAsFieldsAndTheFirstAsTheDisplayName()
    {
        var record = ArenaRecordSource.BuildInfRecord(
            "T.INF",
            Inf("@TEXT\n*TEXT 0\nA nearby sign reads:\n*TEXT 7\nThe floor is oddly patterned.\n"));

        Assert.Equal("A nearby sign reads:", record.FullName);
        Assert.Equal("A nearby sign reads:", record.Fields["Text000"]);
        Assert.Equal("The floor is oddly patterned.", record.Fields["Text007"]);
    }

    [Fact]
    public void BuildInfRecord_ReportsDoorKeysAndRiddleCount()
    {
        var record = ArenaRecordSource.BuildInfRecord(
            "T.INF",
            Inf("@TEXT\n*TEXT 3\n+123\nLocked.\n*TEXT 4\n^2 5\nRiddle?\n:yes\n"));

        Assert.Equal("3:+123", record.Fields["DoorKeys"]);
        Assert.Equal(1, record.Fields["Riddles"]);
    }

    [Fact]
    public void BuildInfRecord_FormId_DependsOnTheNameOnly()
    {
        var first = ArenaRecordSource.BuildInfRecord("CRYSTAL3.INF", Inf("@FLOORS\na.img\n"));
        var second = ArenaRecordSource.BuildInfRecord("CRYSTAL3.INF", Inf("@FLOORS\nb.img\nc.img\n"));
        var other = ArenaRecordSource.BuildInfRecord("AGTEMPL.INF", Inf("@FLOORS\na.img\n"));

        // Same name, different content: the record identity is the file, so the id must not move.
        Assert.Equal(first.FormId, second.FormId);
        Assert.NotEqual(first.FormId, other.FormId);
    }

    [Fact]
    public void BuildInfRecord_FormId_IsCaseInsensitiveOnTheName()
    {
        Assert.Equal(
            ArenaRecordSource.BuildInfRecord("Crystal3.inf", Inf("@FLOORS\na.img\n")).FormId,
            ArenaRecordSource.BuildInfRecord("CRYSTAL3.INF", Inf("@FLOORS\na.img\n")).FormId);
    }

    [Fact]
    public void BuildTemplateRecord_ComposesItsIdFromKeyLetterAndCopy()
    {
        var entries = ArenaTemplateDat
            .ParseText("#0000a\r\ntemperate&\r\n#0000a\r\ndesert&\r\n#0000b\r\nother&\r\n")
            .Entries;

        var ids = entries.Select(e => ArenaRecordSource.BuildTemplateRecord(e).FormId).ToList();

        Assert.Equal(3, ids.Distinct().Count());
        foreach (var id in ids)
        {
            Assert.Equal(ArenaRecordSource.TemplateDomain, ClassicFormIdScheme.DomainOf(id));
        }

        // Key 0, letter 'a', copy 0 → index (0 << 8) | (0 << 5) | 1.
        Assert.Equal(1u, ClassicFormIdScheme.IndexOf(ids[0]));

        // Same key and letter, tileset copy 1 → index (1 << 5) | 1.
        Assert.Equal((1u << 5) | 1u, ClassicFormIdScheme.IndexOf(ids[1]));

        // Key 0, letter 'b' → index 2.
        Assert.Equal(2u, ClassicFormIdScheme.IndexOf(ids[2]));
    }

    [Fact]
    public void BuildTemplateRecord_MaxRetailKey_StaysInsideTheStableIndexRange()
    {
        // The retail file's highest key is 1501; the composition must not overflow 24 bits.
        var entry = Assert.Single(ArenaTemplateDat.ParseText("#1501\r\nlast&\r\n").Entries);

        var record = ArenaRecordSource.BuildTemplateRecord(entry);

        Assert.True(ClassicFormIdScheme.IndexOf(record.FormId) <= ClassicFormIdScheme.MaxIndex);
        Assert.Equal(1501u << 8, ClassicFormIdScheme.IndexOf(record.FormId));
    }

    [Fact]
    public void BuildTemplateRecord_CarriesTheValuesAndADisplayName()
    {
        var entry = Assert.Single(ArenaTemplateDat.ParseText("#0042\r\nfirst&second&\r\n").Entries);

        var record = ArenaRecordSource.BuildTemplateRecord(entry);

        Assert.Equal("ATPL", record.RecordType);
        Assert.Equal("#0042", record.EditorId);
        Assert.Equal("first", record.FullName);
        Assert.Equal(2, record.Fields["Values"]);
        Assert.Equal("first", record.Fields["Value00"]);
        Assert.Equal("second", record.Fields["Value01"]);
    }

    [Fact]
    public void BuildTemplateRecord_TilesetCopy_GetsADistinctEditorId()
    {
        var entries = ArenaTemplateDat.ParseText("#0000\r\none&\r\n#0000\r\ntwo&\r\n").Entries;

        Assert.Equal("#0000", ArenaRecordSource.BuildTemplateRecord(entries[0]).EditorId);
        Assert.Equal("#0000#1", ArenaRecordSource.BuildTemplateRecord(entries[1]).EditorId);
    }

    [Fact]
    public void SynthesizedDomains_AreDistinct_SoRecordTypesNeverCollide()
    {
        var domains = new[]
        {
            ArenaRecordSource.InfDomain,
            ArenaRecordSource.TemplateDomain,
            ArenaRecordSource.LocationDomain,
            ArenaRecordSource.ProvinceDomain
        };

        Assert.Equal(domains.Length, domains.Distinct().Count());
    }

    private static ArenaProvince Province(string name, int index, params (string Name, int Slot)[] locations)
    {
        var slots = new List<ArenaLocation>();
        for (var slot = 0; slot < ArenaCityDataFile.LocationsPerProvince; slot++)
        {
            var authored = locations.FirstOrDefault(l => l.Slot == slot);
            slots.Add(new ArenaLocation(
                authored.Name ?? string.Empty,
                ArenaCityDataFile.KindOfSlot(slot),
                slot,
                100 + slot,
                200 + slot,
                0));
        }

        return new ArenaProvince(name, index, 1, 2, 3, 4, slots);
    }

    [Fact]
    public void BuildProvinceRecord_CountsItsNamedLocationsByKind()
    {
        var province = Province("Skyrim", 2, ("Solitude", 0), ("Dragonstar", 8), ("Karthwasten", 16));

        var record = ArenaRecordSource.BuildProvinceRecord(province);

        Assert.Equal("APRV", record.RecordType);
        Assert.Equal("Skyrim", record.EditorId);
        Assert.Equal("Skyrim", record.FullName);
        Assert.Equal(ArenaRecordSource.ProvinceDomain, ClassicFormIdScheme.DomainOf(record.FormId));
        Assert.Equal(2, record.Fields["ProvinceIndex"]);
        Assert.Equal(3, record.Fields["NamedLocations"]);
        Assert.Equal(1, record.Fields["CityState"]);
        Assert.Equal(1, record.Fields["Town"]);
        Assert.Equal(1, record.Fields["Village"]);
    }

    [Fact]
    public void BuildLocationRecord_ComposesItsIdFromProvinceAndSlot()
    {
        var province = Province("Skyrim", 2, ("Solitude", 0));
        var location = province.Locations[0];

        var record = ArenaRecordSource.BuildLocationRecord(province, location);

        Assert.Equal("ALOC", record.RecordType);
        Assert.Equal("Solitude", record.FullName);
        Assert.Equal("Skyrim_Solitude", record.EditorId);
        Assert.Equal(ArenaRecordSource.LocationDomain, ClassicFormIdScheme.DomainOf(record.FormId));

        // Province 2, slot 0 -> index (2 << 8) | 0.
        Assert.Equal(2u << 8, ClassicFormIdScheme.IndexOf(record.FormId));
        Assert.Equal("Skyrim", record.Fields["Province"]);
        Assert.Equal("CityState", record.Fields["Kind"]);
    }

    [Fact]
    public void BuildLocationRecord_ReportsVisibilityOnlyForDungeons()
    {
        var province = Province("Skyrim", 2, ("Solitude", 0), ("Labyrinthian", 32));

        var city = ArenaRecordSource.BuildLocationRecord(province, province.Locations[0]);
        var dungeon = ArenaRecordSource.BuildLocationRecord(province, province.Locations[32]);

        // Settlements are always drawn, so the flag would be noise on them.
        Assert.False(city.Fields.ContainsKey("Visible"));
        Assert.True(dungeon.Fields.ContainsKey("Visible"));
        Assert.Equal("StaffDungeon", dungeon.Fields["Kind"]);
    }

    [Fact]
    public void LocationIds_AreUniqueAcrossProvincesAndSlots()
    {
        var ids = new List<uint>();
        for (var p = 0; p < ArenaCityDataFile.ProvinceCount; p++)
        {
            var province = Province($"P{p}", p, Enumerable.Range(0, ArenaCityDataFile.LocationsPerProvince)
                .Select(s => ($"L{s}", s)).ToArray());
            ids.AddRange(province.Locations.Select(l =>
                ArenaRecordSource.BuildLocationRecord(province, l).FormId));
        }

        Assert.Equal(ArenaCityDataFile.ProvinceCount * ArenaCityDataFile.LocationsPerProvince, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}

using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) that the retail Shadowkey application directory
///     becomes the census the byte-level RE measured: 21 zones, 8,258 placements, 256 trigger
///     rectangles, 351 surfaces and the two global packs.
///     <para>
///         The install is mounted straight through <see cref="LooseFileSystem" /> rather than
///         through <c>ClassicGameAnalyzer</c>, so what these tests exercise is the record source
///         itself and not the profile/locator wiring that lands separately.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ShadowkeyRecordSourceRetailTests
{
    /// <summary>The 21 <c>.zon</c> stems, spelled as the files are — three differ from their directories.</summary>
    private static readonly string[] Stems =
    [
        "azra", "broken1", "broken2", "crypt1", "crypt2", "Crypt3", "delfhide", "drgnfld", "dstar_e",
        "dstar_w", "erthcave", "fearfrst", "ffarena", "GhstPass", "GlacierCrawl", "lakvan", "LothCav",
        "raiders", "snowline", "stouttp", "twilite"
    ];

    [Fact]
    public void TheRetailInstallSynthesizesTheMeasuredCensusWithUniqueIds()
    {
        var records = Populate();

        Assert.Equal(21, Count(records, ShadowkeyRecordSource.ZoneRecordType));
        Assert.Equal(8258, Count(records, ShadowkeyRecordSource.EntityRecordType));
        Assert.Equal(256, Count(records, ShadowkeyRecordSource.TriggerRecordType));
        Assert.Equal(351, Count(records, ShadowkeyRecordSource.SurfaceRecordType));

        // 237 mesh slots less the 11 that were never packed; 384 sprite slots less the 131 reserved.
        Assert.Equal(226, Count(records, ShadowkeyRecordSource.MeshRecordType));
        Assert.Equal(253, Count(records, ShadowkeyRecordSource.SpriteRecordType));
        Assert.Equal(9365, records.Count);

        // The whole point of the "fail loudly rather than renumber" contract: no two records of any
        // family, in any domain, share an id.
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
        Assert.All(records, r => Assert.InRange(
            ClassicFormIdScheme.DomainOf(r.FormId),
            ShadowkeyRecordSource.FirstDomain,
            ShadowkeyRecordSource.LastDomain));
    }

    [Fact]
    public void TheTwentyOneZoneStemsAreDistinctAtTheTwelveBitsTheIndexSplitSpends()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        // The index rule is hash12(stem) << 12 | rowIndex, so the whole scheme rests on the stems
        // being distinct at 12 bits. They are NOT at 8 (two collide), which is why the split is
        // where it is — and this is measured here rather than assumed by the production code.
        var narrow = Stems.Select(s => ClassicNameHash.Of(s, 8)).Distinct().Count();
        Assert.Equal(19, narrow);

        var wide = Stems.Select(s => ClassicNameHash.Of(s, ShadowkeyRecordSource.StemHashBits)).ToList();
        Assert.Equal(Stems.Length, wide.Distinct().Count());
        Assert.Equal(Stems.Length, Stems.Select(s => ClassicNameHash.Of(s, 24)).Distinct().Count());

        // And every zone's largest table fits its half of the index.
        Assert.True(1071 < 1 << ShadowkeyRecordSource.EntityIndexBits);
        Assert.True(33 < 1 << ShadowkeyRecordSource.ZoneTableIndexBits);
    }

    [Fact]
    public void EveryZoneRecordCarriesItsMapPaletteAndTableCounts()
    {
        var zones = Populate()
            .Where(r => r.RecordType == ShadowkeyRecordSource.ZoneRecordType)
            .ToDictionary(r => (string)r.Fields["Stem"]!, StringComparer.Ordinal);

        Assert.Equal(Stems.OrderBy(s => s, StringComparer.Ordinal), zones.Keys.OrderBy(s => s, StringComparer.Ordinal));
        Assert.All(zones.Values, z => Assert.False(z.Fields.ContainsKey("MissingFiles")));

        // Zone-level totals the per-zone families do not restate: 59 lock rows, 7 paths holding 86
        // points, 326 textures and 331,776 map cells (20 zones at 128x128 plus ffarena at 64x64).
        Assert.Equal(59, zones.Values.Sum(z => (int)z.Fields["LockCount"]!));
        Assert.Equal(7, zones.Values.Sum(z => (int)z.Fields["PathCount"]!));
        Assert.Equal(86, zones.Values.Sum(z => (int)z.Fields["PathPointCount"]!));
        Assert.Equal(326, zones.Values.Sum(z => (int)z.Fields["TextureCount"]!));
        Assert.Equal(331776, zones.Values.Sum(z => (int)z.Fields["MapCells"]!));
        Assert.Equal(64, zones["ffarena"].Fields["MapWidth"]);
        Assert.Equal(20, zones.Values.Count(z => (int)z.Fields["MapWidth"]! == 128));

        // Palettes: RGB888 in 21/21 — the repo's 6-bit range sniff must leave these unpromoted —
        // and the magenta colour key sits at entry 6 in 13 of them and is absent in the other 8.
        Assert.All(zones.Values, z => Assert.True(Assert.IsType<bool>(z.Fields["PaletteIs8Bit"])));
        Assert.Equal(13, zones.Values.Count(z => (bool)z.Fields["PaletteHasColourKey"]!));
        Assert.All(
            zones.Values.Where(z => (bool)z.Fields["PaletteHasColourKey"]!),
            z => Assert.Equal(6, z.Fields["PaletteColourKeyIndex"]));

        // azra alone ships a .sta — 201 records of the 32-byte core, two months older than its .ent.
        Assert.Equal(201, zones["azra"].Fields["StatePlacementRecords"]);
        Assert.Equal(1, zones.Values.Count(z => z.Fields.ContainsKey("StatePlacementRecords")));

        // The lock table rides inline on the zone: 8 rows for delfhide, 17 for dstar_e.
        Assert.Contains("resistDisarm[", (string)zones["dstar_e"].Fields["Locks"]!, StringComparison.Ordinal);
        Assert.Equal("UmbraKeth (32 points)", zones["crypt1"].Fields["Paths"]);
    }

    [Fact]
    public void EveryPlacementResolvesThroughEntitiesTxtToAModelTheZoneActuallyLoads()
    {
        var placements = Populate()
            .Where(r => r.RecordType == ShadowkeyRecordSource.EntityRecordType)
            .ToList();

        // 8,258 of 8,258 ids exist in entities.txt, and every one of the model slots they name is
        // non-NULL in that zone's residency list. Both are what make the chain worth trusting.
        Assert.All(placements, p => Assert.True(Assert.IsType<bool>(p.Fields["EntityResolved"])));
        Assert.All(placements, p => Assert.True(Assert.IsType<bool>(p.Fields["ModelResident"])));

        // 1,012 placements override the entity's shared script with a per-instance one.
        Assert.Equal(1012, placements.Count(p => (bool)p.Fields["ScriptOverride"]!));

        // raiders is the largest zone; its 1,072 placements are what set the 12-bit index budget.
        Assert.Equal(1072, placements.Count(p => (string?)p.Fields["Zone"] == "raiders"));

        // The 8-byte instance name is a truncating field: 41 records hold "Containe" with no NUL.
        Assert.Equal(41, placements.Count(p => (string?)p.Fields["InstanceName"] == "Containe"));
    }

    [Fact]
    public void EverySurfaceIndexesATextureItsZoneActuallyShips()
    {
        var records = Populate();
        var surfaces = records
            .Where(r => r.RecordType == ShadowkeyRecordSource.SurfaceRecordType)
            .ToList();

        // The cross-file identity that fixed the record's field order: the LAST byte is the index,
        // and it is bounded by the zone's inflated .ztx count in 21/21.
        Assert.Equal(351, surfaces.Count);
        Assert.All(surfaces, s => Assert.True(Assert.IsType<bool>(s.Fields["TextureIndexInRange"])));

        var zones = records
            .Where(r => r.RecordType == ShadowkeyRecordSource.ZoneRecordType)
            .ToDictionary(r => (string)r.Fields["Stem"]!, StringComparer.Ordinal);
        foreach (var (stem, zone) in zones)
        {
            // Stronger than "in range": every zone uses its highest texture, so max == count - 1.
            Assert.Equal((int)zone.Fields["TextureCount"]! - 1, (int)zone.Fields["MaxSurfaceTextureIndex"]!);
            Assert.Equal(
                (int)zone.Fields["SurfaceCount"]!,
                surfaces.Count(s => (string?)s.Fields["Zone"] == stem));
        }
    }

    [Fact]
    public void TriggerRectanglesStayInsideTheirZonesMap()
    {
        var records = Populate();
        var mapWidth = records
            .Where(r => r.RecordType == ShadowkeyRecordSource.ZoneRecordType)
            .ToDictionary(r => (string)r.Fields["Stem"]!, r => (int)r.Fields["MapWidth"]!, StringComparer.Ordinal);

        var triggers = records.Where(r => r.RecordType == ShadowkeyRecordSource.TriggerRecordType).ToList();
        Assert.Equal(256, triggers.Count);
        Assert.All(triggers, t =>
        {
            var side = mapWidth[(string)t.Fields["Zone"]!];
            Assert.InRange((ushort)t.Fields["X1"]!, (ushort)t.Fields["X0"]!, (ushort)(side - 1));
            Assert.InRange((ushort)t.Fields["Y1"]!, (ushort)t.Fields["Y0"]!, (ushort)(side - 1));
        });

        // ffarena's 64-tile map is the one that would expose a hard-coded 128.
        Assert.All(
            triggers.Where(t => (string?)t.Fields["Zone"] == "ffarena"),
            t => Assert.True((ushort)t.Fields["X1"]! < 64));
    }

    [Fact]
    public void TheGlobalPacksAreKeyedBySlotAndCountTheZonesThatLoadThem()
    {
        var records = Populate();

        var meshes = records.Where(r => r.RecordType == ShadowkeyRecordSource.MeshRecordType).ToList();
        Assert.Equal(226, meshes.Count);
        Assert.All(meshes, m => Assert.InRange((int)m.Fields["Slot"]!, 0, 236));
        Assert.Equal(226, meshes.Select(m => m.Fields["Slot"]).Distinct().Count());

        // 189 of the slots are loaded by at least one of the 21 zones; the rest are packed but
        // never listed, which a residency mask over one shared slot space makes visible.
        Assert.Equal(189, meshes.Count(m => (int)m.Fields["ZonesLoading"]! > 0));
        Assert.Equal(21, meshes.Max(m => (int)m.Fields["ZonesLoading"]!));

        // The largest record in the pack: 19 alternative skins over a 144-frame animated body.
        var tunic = meshes.Single(m => m.FullName == "male_long_tunic.bin");
        Assert.Equal(144, tunic.Fields["Frames"]);
        Assert.Equal(19, tunic.Fields["Skins"]);

        var sprites = records.Where(r => r.RecordType == ShadowkeyRecordSource.SpriteRecordType).ToList();
        Assert.Equal(253, sprites.Count);
        Assert.All(sprites, s => Assert.InRange((int)s.Fields["Slot"]!, 0, 252));
        Assert.Equal(215, sprites.Count(s => (int)s.Fields["ZonesListing"]! > 0));

        // 117 sprites are the full 176x208 N-Gage screen.
        Assert.Equal(117, sprites.Count(s => (int)s.Fields["Width"]! == 176 && (int)s.Fields["Height"]! == 208));

        // Both packs share domain 0x4B and never collide: meshes at the slot, sprites above them.
        Assert.All(meshes.Concat(sprites),
            r => Assert.Equal(ShadowkeyRecordSource.PackDomain, ClassicFormIdScheme.DomainOf(r.FormId)));
        Assert.Equal(479, meshes.Concat(sprites).Select(r => r.FormId).Distinct().Count());
    }

    private static List<GenericEsmRecord> Populate()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Shadowkey (N-Gage) application directory"));
        Assert.SkipWhen(
            !File.Exists(Path.Combine(root, "azra.zon")),
            RealAssetPaths.SkipMessage("Shadowkey zone files"));

        var records = new RecordCollection();
        using var install = new LooseFileSystem(root);
        ShadowkeyRecordSource.Populate(install, records);
        return records.GenericRecords;
    }

    private static int Count(IEnumerable<GenericEsmRecord> records, string recordType)
    {
        return records.Count(r => r.RecordType == recordType);
    }
}

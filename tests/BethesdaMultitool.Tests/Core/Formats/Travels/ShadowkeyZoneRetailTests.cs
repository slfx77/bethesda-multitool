using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Real-asset sweep of the Shadowkey (N-Gage) per-zone small files over all 21 retail zones of
///     <c>system/apps/6R51/</c>. Opt-in: set <c>RUN_BUCKET_B=1</c>.
///     <para>
///         Every number pinned here was read off the retail bytes on 2026-09-05 and the install is
///         a fixed fixture, so exact counts are legitimate. The sweep is what makes the reader
///         worth anything: the six kinds are accepted purely on tiling, so if a record size or the
///         endianness were wrong, the totals below could not come out.
///     </para>
///     <para>
///         Two cross-file identities carry most of the weight. The <c>.sur</c> texture index is the
///         record's LAST byte, and reading it there makes <c>max(index) == textureCount - 1</c> in
///         21/21 zones against byte 0 of the inflated <c>.ztx</c> — that is what fixes the field
///         order. And the placement chain <c>.ent</c> id → <c>entities.txt</c> → model index →
///         <c>&lt;zone&gt;_models.txt</c> completes for all 8,258 placements, landing every time on
///         a slot the zone actually loads, which is what makes the zone list a residency mask over
///         the global model index rather than a table of its own.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ShadowkeyZoneRetailTests
{
    /// <summary>
    ///     The per-zone census of section 0 of the spec, re-measured from the bytes: trigger
    ///     rectangles, lock rows, paths, path points, surfaces, entity placements and the zone's
    ///     <c>.ztx</c> texture count.
    /// </summary>
    private static readonly ZoneCensus[] Census =
    [
        new("azra", 17, 0, 0, 0, 23, 282, 21),
        new("broken1", 34, 3, 0, 0, 21, 278, 19),
        new("broken2", 27, 0, 0, 0, 18, 162, 18),
        new("crypt1", 31, 5, 1, 32, 17, 438, 17),
        new("crypt2", 15, 1, 1, 31, 18, 292, 16),
        new("Crypt3", 8, 0, 1, 23, 17, 129, 18),
        new("delfhide", 14, 8, 1, 0, 21, 760, 18),
        new("drgnfld", 5, 0, 2, 0, 15, 321, 15),
        new("dstar_e", 10, 17, 0, 0, 21, 715, 22),
        new("dstar_w", 11, 14, 0, 0, 21, 1045, 19),
        new("erthcave", 5, 1, 0, 0, 14, 237, 15),
        new("fearfrst", 5, 0, 0, 0, 14, 221, 10),
        new("ffarena", 3, 0, 1, 0, 7, 41, 7),
        new("GhstPass", 11, 0, 0, 0, 20, 315, 19),
        new("GlacierCrawl", 5, 0, 0, 0, 7, 210, 6),
        new("lakvan", 10, 5, 0, 0, 15, 755, 12),
        new("LothCav", 7, 0, 0, 0, 9, 375, 8),
        new("raiders", 6, 5, 0, 0, 12, 1072, 12),
        new("snowline", 6, 0, 0, 0, 27, 108, 19),
        new("stouttp", 6, 0, 0, 0, 14, 81, 14),
        new("twilite", 20, 0, 0, 0, 20, 421, 21)
    ];

    /// <summary>
    ///     All 21 zones parse, tile exactly, and match the measured per-zone census — plus the
    ///     totals: 256 trigger rectangles, 59 lock rows, 7 paths holding 86 points, 351 surfaces
    ///     and 8,258 entity placements.
    /// </summary>
    [Fact]
    public void AllTwentyOneZones_TileExactlyAndMatchTheCensus()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        Assert.Equal(21, Directory.GetFiles(root, "*.zon").Length);

        var totals = (Triggers: 0, Locks: 0, Paths: 0, Points: 0, Surfaces: 0, Entities: 0);
        foreach (var zone in Census)
        {
            var triggers = ShadowkeyZoneFiles.ParseZon(Read(root, zone.Stem, ".zon"), zone.Stem + ".zon");
            var locks = ShadowkeyZoneFiles.ParseStn(Read(root, zone.Stem, ".stn"), zone.Stem + ".stn");
            var paths = ShadowkeyZoneFiles.ParsePth(Read(root, zone.Stem, ".pth"), zone.Stem + ".pth");
            var surfaces = ShadowkeyZoneFiles.ParseSur(Read(root, zone.Stem, ".sur"), zone.Stem + ".sur");
            var entities = ShadowkeyZoneFiles.ParseEnt(Read(root, zone.Stem, ".ent"), zone.Stem + ".ent");

            var points = paths.Sum(p => p.Points.Count);
            Assert.Equal(
                (zone.Stem, zone.Triggers, zone.Locks, zone.Paths, zone.PathPoints, zone.Surfaces, zone.Entities),
                (zone.Stem, triggers.Count, locks.Count, paths.Count, points, surfaces.Count, entities.Entities.Count));
            Assert.True(entities.HasNames);

            totals.Triggers += triggers.Count;
            totals.Locks += locks.Count;
            totals.Paths += paths.Count;
            totals.Points += points;
            totals.Surfaces += surfaces.Count;
            totals.Entities += entities.Entities.Count;
        }

        Assert.Equal((256, 59, 7, 86, 351, 8258), totals);
    }

    /// <summary>
    ///     The retail traps, sampled where they actually occur: 41 records hold an 8-byte instance
    ///     name with no NUL, one script field carries a 0xFF byte, and the alignment slot before
    ///     the entity id is 0xCCCC in every one of the 8,258 records.
    /// </summary>
    [Fact]
    public void EntityNamesAreTruncatedAndTextIsLatin1()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var eightByteNames = 0;
        var nonAsciiScripts = 0;
        var padSlots = 0;
        var records = 0;
        foreach (var zone in Census)
        {
            var raw = Read(root, zone.Stem, ".ent");
            var entities = ShadowkeyZoneFiles.ParseEnt(raw, zone.Stem + ".ent");
            for (var i = 0; i < entities.Entities.Count; i++)
            {
                var entity = entities.Entities[i];
                records++;
                if (entity.Name.Length == ShadowkeyZoneFiles.InstanceNameLength)
                {
                    eightByteNames++;
                }

                if (entity.Script.Any(c => c > 0x7E))
                {
                    nonAsciiScripts++;
                }

                var at = 4 + i * ShadowkeyZoneFiles.EntityRecordLength + 26;
                if (raw[at] == 0xCC && raw[at + 1] == 0xCC)
                {
                    padSlots++;
                }
            }
        }

        Assert.Equal(8258, records);
        Assert.Equal(8258, padSlots);
        Assert.Equal(41, eightByteNames);
        Assert.Equal(1, nonAsciiScripts);
    }

    /// <summary>
    ///     All 8,258 placements resolve through <c>entities.txt</c>, and every one of them lands on
    ///     a model slot its own zone loads — no placement points at a <c>NULL.bin</c> slot.
    /// </summary>
    [Fact]
    public void EveryPlacementResolvesToAModelItsZoneLoads()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var entityTable = ShadowkeyTextTables.ParseEntities(
            File.ReadAllBytes(Path.Combine(root, "entities.txt")), "entities.txt");
        Assert.Equal(735, entityTable.Entities.Count);
        Assert.Equal(6023u, entityTable.Entities.Max(e => e.Id));

        var globalModels = ShadowkeyTextTables.ParseModels(
            File.ReadAllBytes(Path.Combine(root, "models.txt")), "models.txt");
        Assert.Equal(237, globalModels.Models.Count);

        var resolved = 0;
        var resident = 0;
        foreach (var zone in Census)
        {
            var zoneModelsName = zone.Stem + "_models.txt";
            var zoneModels = ShadowkeyTextTables.ParseModels(
                File.ReadAllBytes(Path.Combine(root, zoneModelsName)), zoneModelsName);
            Assert.Equal(236, zoneModels.Models.Count);
            Assert.InRange(zoneModels.ResidentCount, 1, 235);

            var entities = ShadowkeyZoneFiles.ParseEnt(Read(root, zone.Stem, ".ent"), zone.Stem + ".ent");
            foreach (var entity in entities.Entities)
            {
                var resolution = ShadowkeyTextTables.Resolve(entity.EntityId, entityTable, zoneModels);
                if (resolution.EntityFound)
                {
                    resolved++;
                }

                if (resolution.IsResident)
                {
                    resident++;
                }
            }
        }

        Assert.Equal(8258, resolved);
        Assert.Equal(8258, resident);
    }

    /// <summary>
    ///     The decisive cross-file identity: the LAST byte of a <c>.sur</c> record indexes the
    ///     zone's texture set, and its maximum is exactly one below the count byte of the inflated
    ///     <c>.ztx</c> in all 21 zones (azra 20 of 21, ffarena 6 of 7, GlacierCrawl 5 of 6,
    ///     dstar_e 21 of 22). The <c>.ztx</c> also inflates to <c>1 + count * 16384</c> bytes —
    ///     128x128 8-bit textures — which is what makes byte 0 a count in the first place.
    /// </summary>
    [Fact]
    public void MaxSurfaceTextureIndexIsOneBelowTheZoneTextureCount()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        foreach (var zone in Census)
        {
            var textures = ShadowkeyCompressedFile.Inflate(Read(root, zone.Stem, ".ztx"), zone.Stem + ".ztx");
            int textureCount = textures[0];
            Assert.Equal(zone.Textures, textureCount);
            Assert.Equal(1 + textureCount * 128 * 128, textures.Length);

            var surfaces = ShadowkeyZoneFiles.ParseSur(Read(root, zone.Stem, ".sur"), zone.Stem + ".sur");
            Assert.Equal(textureCount - 1, surfaces.Max(s => (int)s.TextureIndex));
        }
    }

    /// <summary>
    ///     Every <c>.stn</c> row is a <c>resistDisarm[N]</c> naming an instance that really exists
    ///     in the same zone's <c>.ent</c> — the join is by name, since the file holds no index.
    /// </summary>
    [Fact]
    public void EveryLockRowNamesAnInstanceOfItsOwnZone()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var rows = 0;
        foreach (var zone in Census)
        {
            var locks = ShadowkeyZoneFiles.ParseStn(Read(root, zone.Stem, ".stn"), zone.Stem + ".stn");
            if (locks.Count == 0)
            {
                continue;
            }

            var names = ShadowkeyZoneFiles.ParseEnt(Read(root, zone.Stem, ".ent"), zone.Stem + ".ent")
                .Entities.Select(e => e.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var entry in locks)
            {
                rows++;
                Assert.NotNull(entry.ResistDisarm);
                Assert.InRange(entry.ResistDisarm!.Value, 2, 30);
                Assert.Contains(entry.EntityName, names);
            }
        }

        Assert.Equal(59, rows);
    }

    /// <summary>
    ///     <c>azra.sta</c> — the only one in the install — is 201 records of the 32-byte
    ///     <c>.ent</c> prefix (6,436 bytes), all of whose ids resolve in <c>entities.txt</c>, and
    ///     it carries no names.
    /// </summary>
    [Fact]
    public void AzraSta_Is201ThirtyTwoByteCores()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        Assert.Single(Directory.GetFiles(root, "*.sta"));

        var raw = Read(root, "azra", ".sta");
        Assert.Equal(6436, raw.Length);

        var list = ShadowkeyZoneFiles.ParseSta(raw, "azra.sta");
        Assert.Equal(201, list.Entities.Count);
        Assert.False(list.HasNames);
        Assert.All(list.Entities, e => Assert.Equal(string.Empty, e.Name));

        var entityTable = ShadowkeyTextTables.ParseEntities(
            File.ReadAllBytes(Path.Combine(root, "entities.txt")), "entities.txt");
        Assert.All(list.Entities, e => Assert.NotNull(entityTable.Find(e.EntityId)));
    }

    private static byte[] Read(string root, string stem, string extension)
    {
        return File.ReadAllBytes(Path.Combine(root, stem + extension));
    }

    private static string RequireRoot()
    {
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(
            root is null,
            "The Shadowkey (N-Gage) application directory is not staged under Sample/Builds.");
        return root;
    }

    private sealed record ZoneCensus(
        string Stem,
        int Triggers,
        int Locks,
        int Paths,
        int PathPoints,
        int Surfaces,
        int Entities,
        int Textures);
}
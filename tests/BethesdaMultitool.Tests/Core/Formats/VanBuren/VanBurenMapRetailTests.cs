using BethesdaMultitool.Core.Formats.VanBuren;
using BethesdaMultitool.Core.Rendering.Level2D;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the Van Buren <c>EMAP</c> maps, their walk grids
///     and <c>resource.rht</c> against the staged Dec 9 2003 prototype. Every figure pinned here was
///     measured on 2026-09-08 with an independent Python walk before the C# readers were written.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class VanBurenMapRetailTests
{
    private static string RequireBuildRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.VanBuren();
        Assert.SkipWhen(root is null || !Directory.Exists(Path.Combine(root, "data")),
            RealAssetPaths.SkipMessage("Van Buren prototype"));
        return root;
    }

    private static (byte[] Bytes, VanBurenGrpArchive Archive) OpenGroup(string root, string group)
    {
        var path = Path.Combine(root, "data", group + ".grp");
        var bytes = File.ReadAllBytes(path);
        return (bytes, VanBurenGrpArchive.Parse(bytes, group + ".grp"));
    }

    private static IEnumerable<(string Group, VanBurenGrpEntry Entry, byte[] Payload)> Payloads(string root, string tag)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(root, "data"), "*.grp"))
        {
            var group = Path.GetFileNameWithoutExtension(file);
            var (bytes, archive) = OpenGroup(root, group);
            foreach (var entry in archive.Entries.Where(e => e.Tag == tag))
            {
                yield return (group, entry, VanBurenGrpArchive.Read(bytes, entry));
            }
        }
    }

    private static VanBurenMapLevel Resolve(string group, byte[] archiveBytes, VanBurenGrpArchive archive,
        VanBurenGrpEntry entry, VanBurenResourceIndex? index)
    {
        var map = VanBurenMapFile.Parse(VanBurenGrpArchive.Read(archiveBytes, entry), entry.Name);
        var byName = archive.Entries.ToDictionary(e => e.Name);
        return VanBurenMapCompanions.Resolve(
            map, group,
            archive.Entries.Select(e => new VanBurenMapCompanions.Candidate(e.Index, e.Name, e.Tag)).ToList(),
            name => byName.TryGetValue(name, out var e) ? VanBurenGrpArchive.Read(archiveBytes, e) : null,
            index);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "S1244",
        Justification = "Counts exact authored header constants for the pinned retail census.")]
    public void EveryMapTilesAsAChunkStreamWithThePinnedCensus()
    {
        var root = RequireBuildRoot();
        var tags = new Dictionary<string, int>();
        var versions = new HashSet<uint>();
        var fourthNames = new HashSet<string>();
        var maps = 0;
        var floats = 0;

        foreach (var (_, entry, payload) in Payloads(root, "EMAP"))
        {
            var map = VanBurenMapFile.Parse(payload, entry.Name);
            maps++;
            versions.Add(map.Header.Version);
            fourthNames.Add(map.Header.FourthName);
            if (map.Header.FloatA == 250f && map.Header.FloatB == 500f && map.Header.FloatC == 1f)
            {
                floats++;
            }

            // Every map names a .8, a .rle and an area-map texture off ONE stem.
            Assert.EndsWith(".8", map.Header.SceneName, StringComparison.Ordinal);
            Assert.EndsWith(".rle", map.Header.WalkGridName, StringComparison.Ordinal);
            Assert.Equal(map.Header.Stem, Path.GetFileNameWithoutExtension(map.Header.WalkGridName));
            Assert.StartsWith(map.Header.Stem + "_AM", map.Header.AreaMapName, StringComparison.Ordinal);
            Assert.Empty(map.UnclaimedTags);

            foreach (var chunk in map.Chunks)
            {
                tags[chunk.Tag] = tags.GetValueOrDefault(chunk.Tag) + 1;
            }
        }

        Assert.Equal(38, maps);
        Assert.Equal([5u], versions);
        Assert.Equal([string.Empty], fourthNames);
        Assert.Equal(36, floats);

        // The chunk census, pinned so a reader that started dropping or duplicating chunks shows.
        Assert.Equal(38, tags["EMAP"]);
        Assert.Equal(38, tags["ECAM"]);
        Assert.Equal(38, tags["EMNP"]);
        Assert.Equal(175, tags["EME2"]);
        Assert.Equal(35, tags["EMEF"]);
        Assert.Equal(28, tags["EMTR"]);
        Assert.Equal(23, tags["EMSD"]);
        Assert.Equal(23, tags["EBTR"]);
        Assert.Equal(14, tags["EMEP"]);
        Assert.Equal(9, tags["EPTH"]);
        Assert.Equal(5, tags["EMNO"]);
        Assert.Equal(3, tags["ESTR"]);
        Assert.Equal(2, tags["ETTR"]);
        Assert.Equal(2, tags["2MWT"]);
        Assert.Equal(14, tags.Count);
    }

    [Fact]
    public void EveryTriggerIsCompletedByTheRecordItsKindOwns()
    {
        // Kind 6 -> EBTR, 1 -> ESTR, 0 -> ETTR on all 28; the pairing is the game's factory, not
        // adjacency luck, so a kind meeting the wrong record must fail this.
        var root = RequireBuildRoot();
        var pairs = new Dictionary<(uint Kind, string Tag), int>();
        var entities = 0;
        var radians = 0;

        foreach (var (_, entry, payload) in Payloads(root, "EMAP"))
        {
            var map = VanBurenMapFile.Parse(payload, entry.Name);
            foreach (var trigger in map.Triggers)
            {
                pairs[(trigger.Kind, trigger.Detail.Tag)] =
                    pairs.GetValueOrDefault((trigger.Kind, trigger.Detail.Tag)) + 1;
                Assert.True(trigger.Points.Count >= 3, $"{entry.Name}: a trigger with {trigger.Points.Count} points");
            }

            foreach (var e in map.Entities)
            {
                entities++;
                Assert.Contains(Path.GetExtension(e.Template).ToUpperInvariant(),
                    new[] { ".CRT", ".DOR", ".CON", ".USE", ".ITM", ".WEA", ".ARM", ".AMO" });
                Assert.NotEmpty(e.InstanceName);
                if (MathF.Abs(e.Rotation.X) <= MathF.PI * 1.5f + 0.01f)
                {
                    radians++;
                }
            }
        }

        Assert.Equal(23, pairs[(6, "EBTR")]);
        Assert.Equal(3, pairs[(1, "ESTR")]);
        Assert.Equal(2, pairs[(0, "ETTR")]);
        Assert.Equal(3, pairs.Count);
        Assert.Equal(175, entities);
        // ⚠ Rotations are radians: every one fits a turn-and-a-half; in degrees they would not.
        Assert.Equal(entities, radians);
    }

    [Fact]
    public void EveryWalkGridTilesAndTheProbeClaimsNothingElse()
    {
        // 39 grids: 38 in Maps.grp and one in Engine.grp — exactly the type-1700 count in
        // resource.rht. The probe is the tiling, so the two OTHER untagged families (Critters 36,
        // Items 83) must be refused by it.
        var root = RequireBuildRoot();
        var grids = new Dictionary<string, int>();
        var dims = new Dictionary<(int, int), int>();
        var dominantBlocked = 0;

        foreach (var file in Directory.GetFiles(Path.Combine(root, "data"), "*.grp"))
        {
            var group = Path.GetFileNameWithoutExtension(file);
            var (bytes, archive) = OpenGroup(root, group);
            foreach (var entry in archive.Entries.Where(e => e.Tag.Contains('.')))
            {
                var payload = VanBurenGrpArchive.Read(bytes, entry);
                if (!VanBurenWalkGrid.TryParse(payload, entry.Name, out var grid, out _))
                {
                    continue;
                }

                grids[group] = grids.GetValueOrDefault(group) + 1;
                dims[(grid.Width, grid.Height)] = dims.GetValueOrDefault((grid.Width, grid.Height)) + 1;
                var blocked = grid.Cells.Count(c => c == VanBurenWalkGrid.Blocked);
                if (blocked * 2 > grid.Cells.Length)
                {
                    dominantBlocked++;
                }
            }
        }

        Assert.Equal(38, grids["Maps"]);
        Assert.Equal(1, grids["Engine"]);
        Assert.Equal(2, grids.Count);
        Assert.Equal(2, dims[(84, 84)]);
        Assert.Equal(2, dims[(644, 644)]);
        Assert.Equal(1, dims[(704, 464)]);
        Assert.Equal(1, dims[(144, 104)]);
        // 47 is the border and the walls; it outweighs the open interior on 14 of the 39.
        Assert.Equal(14, dominantBlocked);
    }

    [Fact]
    public void TheResourceIndexNamesEveryPayloadOfEveryGroup()
    {
        var root = RequireBuildRoot();
        var index = VanBurenResourceIndex.Parse(File.ReadAllBytes(Path.Combine(root, VanBurenResourceIndex.FileName)),
            VanBurenResourceIndex.FileName);

        Assert.Equal(7044, index.Entries.Count);
        Assert.Equal(24, index.Groups.Count);
        Assert.Equal("Tiles", index.Groups[0]);
        Assert.Equal("Maps", index.Groups[6]);

        var covered = 0;
        var total = 0;
        var typesByTag = new Dictionary<string, HashSet<uint>>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "data"), "*.grp"))
        {
            var group = Path.GetFileNameWithoutExtension(file);
            var (_, archive) = OpenGroup(root, group);
            foreach (var entry in archive.Entries)
            {
                total++;
                if (index.Lookup(group, entry.Index) is { } named)
                {
                    covered++;
                    if (!typesByTag.TryGetValue(entry.Tag, out var set))
                    {
                        typesByTag[entry.Tag] = set = [];
                    }

                    set.Add(named.TypeId);
                }
            }
        }

        Assert.Equal(7044, total);
        Assert.Equal(total, covered);
        // The type ids the tags settle: one id per tagged family.
        Assert.Equal([VanBurenResourceIndex.MeshType], typesByTag["B3D"]);
        Assert.Equal([VanBurenResourceIndex.SceneType], typesByTag["8TRE"]);
        Assert.Equal([VanBurenResourceIndex.MapType], typesByTag["EMAP"]);
        Assert.Equal([1100u], typesByTag["RIFF"]);
        Assert.Equal([1500u], typesByTag["VEG"]);
        Assert.Equal([1600u], typesByTag["GUI"]);

        // The names the trio resolves through: the MarkTest map, scene and grid.
        Assert.Equal("zz_TestMapsMarkTest", index.Lookup("Maps", 1)!.Value.Name);
        Assert.Equal([2], index.Find("Maps", "MarkTest", VanBurenResourceIndex.SceneType).Select(e => e.Index));
        Assert.Equal([3], index.Find("Maps", "MarkTest", VanBurenResourceIndex.WalkGridType).Select(e => e.Index));
        Assert.Equal(39, index.Entries.Count(e => e.TypeId == VanBurenResourceIndex.WalkGridType));
    }

    [Fact]
    public void TheIndexAndTheSceneTextureNamesPairTheSameCompanions()
    {
        // ⚑ The control: two INDEPENDENT pairings — the game's index, and the scene's own
        // "<stem>_N.ctx" texture names — must name the same scene entry for every map that has
        // one, and the index's grid must match the scene's LVLD dimensions. 34 of 38 maps have a
        // scene (Test_Building, Test_Junktown_fences_metas, Weapon_Ammo_Item_Test1 and
        // CanyonWater01 ship none); the two frames agree on all 34.
        var root = RequireBuildRoot();
        var index = VanBurenResourceIndex.Parse(File.ReadAllBytes(Path.Combine(root, VanBurenResourceIndex.FileName)),
            VanBurenResourceIndex.FileName);
        var (bytes, archive) = OpenGroup(root, "Maps");

        var withScene = 0;
        var withGrid = 0;
        var agreeing = 0;
        var framed = 0;
        foreach (var entry in archive.Entries.Where(e => e.Tag == "EMAP"))
        {
            var byIndex = Resolve("Maps", bytes, archive, entry, index);
            var byNames = Resolve("Maps", bytes, archive, entry, null);

            if (byIndex.SceneEntryName is not null)
            {
                withScene++;
                Assert.Equal(VanBurenResourceIndex.FileName, byIndex.PairedBy);
                if (byIndex.SceneEntryName == byNames.SceneEntryName)
                {
                    agreeing++;
                }
            }

            if (byIndex.WalkGrid is { } grid)
            {
                withGrid++;
                Assert.Equal(byIndex.GridWidth, grid.Width);
                Assert.Equal(byIndex.GridHeight, grid.Height);
            }

            if (byIndex.HasFrame)
            {
                framed++;
                // The octree's root box spans exactly the LVLD grid at 0.5 units per cell.
                var spanX = byIndex.Extent!.Value.X - byIndex.Origin!.Value.X;
                var spanZ = byIndex.Extent.Value.Z - byIndex.Origin.Value.Z;
                Assert.Equal(byIndex.GridWidth * byIndex.CellSize, spanX, 0.01f);
                Assert.Equal(byIndex.GridHeight * byIndex.CellSize, spanZ, 0.01f);
            }
        }

        Assert.Equal(34, withScene);
        Assert.Equal(34, agreeing);
        Assert.Equal(34, withGrid);
        Assert.Equal(34, framed);
    }

    [Fact]
    public void PlacementsFallInsideTheirOwnSceneFrame()
    {
        // ⚑ A control that can DISCRIMINATE: every placement of a paired map lies inside its own
        // scene's root box (with a one-unit skirt), 19 of 19 maps that place anything. Against
        // every WRONG scene the same test averages 0.55 — so it is not a tautology of the pairing.
        var root = RequireBuildRoot();
        var index = VanBurenResourceIndex.Parse(File.ReadAllBytes(Path.Combine(root, VanBurenResourceIndex.FileName)),
            VanBurenResourceIndex.FileName);
        var (bytes, archive) = OpenGroup(root, "Maps");

        var tested = 0;
        foreach (var entry in archive.Entries.Where(e => e.Tag == "EMAP"))
        {
            var level = Resolve("Maps", bytes, archive, entry, index);
            if (!level.HasFrame)
            {
                continue;
            }

            var points = level.Map.Entities.Select(e => e.Position)
                .Concat(level.Map.Effects.Select(e => e.Position))
                .Concat(level.Map.EntryPoints.Where(p => p.Position != default).Select(p => p.Position))
                .Concat(level.Map.Sounds.Select(s => s.Position))
                .Concat(level.Map.Notes.Select(n => n.Position))
                .Concat(level.Map.Triggers.SelectMany(t => t.Points))
                .Concat(level.Map.Paths.SelectMany(p => p.Points.Select(w => w.Position)))
                .Concat(level.Map.NavPoints.Select(n => n.Position))
                .ToList();
            if (points.Count == 0)
            {
                continue;
            }

            tested++;
            var origin = level.Origin!.Value;
            var extent = level.Extent!.Value;
            var inside = points.Count(p =>
                p.X >= origin.X - 1 && p.X <= extent.X + 1 && p.Z >= origin.Z - 1 && p.Z <= extent.Z + 1);
            Assert.Equal(points.Count, inside);
        }

        Assert.Equal(19, tested);
    }

    [Theory]
    [InlineData("MarkTest", 84, 84, 7, 0)]
    [InlineData("04_Tutorial_Vault", 384, 334, 21, 2)]
    [InlineData("03_Tutorial_Junktown", 384, 624, 35, 8)]
    public void ThreeLevelsRenderTheirGridAndPlan(string stem, int width, int height, int entities, int triggers)
    {
        var root = RequireBuildRoot();
        var index = VanBurenResourceIndex.Parse(File.ReadAllBytes(Path.Combine(root, VanBurenResourceIndex.FileName)),
            VanBurenResourceIndex.FileName);
        var (bytes, archive) = OpenGroup(root, "Maps");
        var mapEntry = archive.Entries.First(e => e.Tag == "EMAP"
                                                  && VanBurenMapFile.Parse(VanBurenGrpArchive.Read(bytes, e), e.Name)
                                                      .Header.Stem == stem);
        var level = Resolve("Maps", bytes, archive, mapEntry, index);

        Assert.Equal(width, level.GridWidth);
        Assert.Equal(height, level.GridHeight);
        Assert.Equal(entities, level.Map.Entities.Count);
        Assert.Equal(triggers, level.Map.Triggers.Count);
        Assert.NotNull(level.WalkGrid);

        var source = new VanBurenMapLevel2DSource(level, 2);
        Assert.Equal([Level2DLayer.Floor, Level2DLayer.Overlay], source.Layers);

        var floor = source.Render(Level2DLayer.Floor)!.Value;
        Assert.Equal(width * 2, floor.Width);
        Assert.Equal(height * 2, floor.Height);
        var plan = source.Render(Level2DLayer.Overlay)!.Value;

        // The plan must differ from the dimmed grid wherever something was drawn — a plan of an
        // inhabited level that equals its background drew nothing.
        var differing = 0;
        for (var i = 0; i < plan.Rgba.Length; i += 4)
        {
            if (plan.Rgba[i] != floor.Rgba[i] / 3 || plan.Rgba[i + 1] != floor.Rgba[i + 1] / 3 ||
                plan.Rgba[i + 2] != floor.Rgba[i + 2] / 3)
            {
                differing++;
            }
        }

        Assert.True(differing > entities * 4, $"{stem}: only {differing} plan pixels differ from the background");
    }
}

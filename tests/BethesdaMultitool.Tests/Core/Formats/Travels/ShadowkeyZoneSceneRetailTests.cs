using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Real-asset checks for the Shadowkey zone-to-scene bridge, over the 21 retail zones of
///     <c>system/apps/6R51/</c>. Opt-in: set <c>RUN_BUCKET_B=1</c>.
///     <para>
///         The first test is the one that matters. The <c>.zcp</c> record stores four floor heights
///         and never says which corner is which, so the assignment had to be measured: two adjacent
///         open cells must agree about the two corners they share, and scoring that over all 24
///         permutations picks exactly one. This test RE-DERIVES the winner from the retail bytes
///         with its own scan and then asserts the builder uses it — so the constant is checked
///         against the data, not against itself.
///     </para>
///     <para>
///         ⚠ Two controls in that scan earn their place. Ceilings are excluded because they are
///         flat almost everywhere, which makes all 24 orderings score an identical 99.7% — a
///         measurement that cannot discriminate is not evidence. And pairs where BOTH prototypes
///         are level are excluded for the same reason: they agree under every ordering, and
///         including them buries the signal (a 40-point separation collapses toward noise as the
///         flat majority dilutes it).
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ShadowkeyZoneSceneRetailTests
{
    /// <summary>All 21 retail zone stems.</summary>
    private static readonly string[] AllZones =
    [
        "azra", "broken1", "broken2", "Crypt3", "crypt1", "crypt2", "delfhide", "drgnfld",
        "dstar_e", "dstar_w", "erthcave", "fearfrst", "ffarena", "GhstPass", "GlacierCrawl",
        "lakvan", "LothCav", "raiders", "snowline", "stouttp", "twilite"
    ];

    /// <summary>
    ///     The five zones with enough sloped ground to decide the corner order. The other sixteen
    ///     are crypts and interiors whose floors are level, so they contribute no discriminating
    ///     pair at all — measured, not assumed: eleven of them produce literally zero.
    /// </summary>
    private static readonly string[] TerrainZones = ["azra", "drgnfld", "GhstPass", "snowline", "stouttp"];

    /// <summary>The four tile corners, in the order the scan indexes them.</summary>
    private static readonly (int Dx, int Dy)[] Corners = [(0, 0), (1, 0), (1, 1), (0, 1)];

    private static string RequireRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Shadowkey (system/apps/6R51)"));
        return root;
    }

    private static byte[] Inflate(string root, string zone, string extension)
    {
        return ShadowkeyCompressedFile.Inflate(
            File.ReadAllBytes(Path.Combine(root, zone + extension)), zone + extension);
    }

    private static (ShadowkeyZoneMap Map, ShadowkeyCellPrototypes Prototypes) Load(string root, string zone)
    {
        return (ShadowkeyZoneMap.Parse(Inflate(root, zone, ".zmp"), zone + ".zmp"),
            ShadowkeyCellPrototypes.Parse(Inflate(root, zone, ".zcp"), zone + ".zcp"));
    }

    /// <summary>
    ///     Scores one slot-to-corner assignment: over every pair of adjacent OPEN cells where at
    ///     least one side is sloped, how often do the two cells agree about the floor height of the
    ///     two corners they share? Deliberately written independently of the builder — it walks the
    ///     grid and the prototype table directly and never calls
    ///     <see cref="ShadowkeyZoneSceneBuilder.CornerOffset" />.
    /// </summary>
    private static (long Agree, long Total) Score(
        ShadowkeyZoneMap map, ShadowkeyCellPrototypes prototypes, int[] permutation)
    {
        var slotOf = new int[4];
        for (var slot = 0; slot < 4; slot++)
        {
            slotOf[Corners[permutation[slot]].Dx * 2 + Corners[permutation[slot]].Dy] = slot;
        }

        int Slot(int dx, int dy)
        {
            return slotOf[dx * 2 + dy];
        }

        long agree = 0;
        long total = 0;
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                var cell = map.Cell(x, y);
                if (cell.IsBlocked)
                {
                    continue;
                }

                var here = prototypes.Records[cell.PrototypeIndex].FloorCorners;
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx >= map.Width || ny >= map.Height)
                    {
                        continue;
                    }

                    var neighbourCell = map.Cell(nx, ny);
                    if (neighbourCell.IsBlocked)
                    {
                        continue;
                    }

                    var there = prototypes.Records[neighbourCell.PrototypeIndex].FloorCorners;
                    if (IsLevel(here) && IsLevel(there))
                    {
                        continue;
                    }

                    // The two shared corners: for an east neighbour the pair runs along y, for a
                    // south neighbour along x.
                    for (var along = 0; along < 2; along++)
                    {
                        var ours = dx == 1 ? Slot(1, along) : Slot(along, 1);
                        var theirs = dx == 1 ? Slot(0, along) : Slot(along, 0);
                        total++;
                        if (here[ours] == there[theirs])
                        {
                            agree++;
                        }
                    }
                }
            }
        }

        return (agree, total);
    }

    private static bool IsLevel(IReadOnlyList<short> corners)
    {
        return corners[0] == corners[1] && corners[1] == corners[2] && corners[2] == corners[3];
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    /// <summary>
    ///     ⚑ The measurement the corner order rests on, re-run against the retail bytes. Exactly one
    ///     of the 24 orderings fits, and it is the one the builder uses.
    /// </summary>
    [Fact]
    public void TheCornerOrder_IsTheOneTheRetailBytesPick()
    {
        var root = RequireRoot();

        var zones = TerrainZones.Select(zone => Load(root, zone)).ToArray();
        var ranked = new List<(int[] Permutation, double Fraction)>();
        foreach (var permutation in Permutations([0, 1, 2, 3]))
        {
            long agree = 0;
            long total = 0;
            foreach (var (map, prototypes) in zones)
            {
                var (a, t) = Score(map, prototypes, permutation);
                agree += a;
                total += t;
            }

            Assert.True(total > 100_000, $"Only {total} discriminating corner pairs — too few to decide.");
            ranked.Add((permutation, (double)agree / total));
        }

        ranked.Sort((left, right) => right.Fraction.CompareTo(left.Fraction));

        // A control that cannot separate the candidates is worthless, so the margin is asserted
        // before the winner is: three rivals still score above 40%, which is exactly why a single
        // plausible-looking ordering would not have been evidence on its own.
        Assert.True(
            ranked[0].Fraction > 0.95,
            $"The best ordering only agrees {ranked[0].Fraction:P2} of the time.");
        Assert.True(
            ranked[0].Fraction - ranked[1].Fraction > 0.30,
            $"The top two orderings are {ranked[0].Fraction:P2} and {ranked[1].Fraction:P2} — too close to decide.");

        for (var slot = 0; slot < 4; slot++)
        {
            Assert.Equal(Corners[ranked[0].Permutation[slot]], ShadowkeyZoneSceneBuilder.CornerOffset(slot));
        }
    }

    /// <summary>
    ///     Ceilings cannot settle the corner order, so this pins the reason they are excluded rather
    ///     than leaving it as a claim in a comment. Retail ceilings are level in the overwhelming
    ///     majority of prototypes, and a level record agrees with itself under every permutation.
    /// </summary>
    [Fact]
    public void CeilingsAreTooFlatToDecideAnything()
    {
        var root = RequireRoot();

        var level = 0;
        var records = 0;
        foreach (var zone in AllZones)
        {
            foreach (var record in Load(root, zone).Prototypes.Records)
            {
                records++;
                if (IsLevel(record.CeilingCorners))
                {
                    level++;
                }
            }
        }

        Assert.Equal(66_829, records);
        Assert.True(
            level > records * 0.8,
            $"Only {level} of {records} ceilings are level; the exclusion argument would not hold.");
    }

    /// <summary>
    ///     Every zone builds, and every part it produces is renderable: indices inside their own
    ///     vertex range, finite positions, one colour and one normal per vertex, and bounds that sit
    ///     inside the grid the map declares.
    /// </summary>
    [Fact]
    public void EveryZoneBuildsIntoRenderableParts()
    {
        var root = RequireRoot();

        var totalParts = 0;
        var totalTriangles = 0L;
        foreach (var zone in AllZones)
        {
            var (map, prototypes) = Load(root, zone);
            var scene = ShadowkeyZoneSceneBuilder.Build(map, prototypes);

            Assert.NotEmpty(scene.MeshParts);
            Assert.NotNull(scene.Bounds);
            var bounds = scene.Bounds!.Value;
            Assert.True(bounds.IsFinite, $"{zone}: bounds are not finite.");
            Assert.InRange(bounds.Minimum.X, 0f, map.Width);
            Assert.InRange(bounds.Maximum.X, 0f, map.Width);
            Assert.InRange(bounds.Minimum.Y, 0f, map.Height);
            Assert.InRange(bounds.Maximum.Y, 0f, map.Height);

            foreach (var part in scene.MeshParts)
            {
                totalParts++;
                var submesh = part.Submesh;
                var vertices = submesh.Positions.Length / 3;
                totalTriangles += submesh.Triangles.Length / 3;

                Assert.True(vertices <= ShadowkeyZoneSceneBuilder.MaxPartVertices, $"{zone}: part overflows.");
                Assert.Equal(submesh.Positions.Length, submesh.Normals!.Length);
                Assert.Equal(vertices * 4, submesh.VertexColors!.Length);
                Assert.All(submesh.Triangles, index => Assert.True(index < vertices, $"{zone}: stray index."));
                Assert.All(submesh.Positions, value => Assert.True(float.IsFinite(value), $"{zone}: bad vertex."));
            }
        }

        // The zones are a fixed fixture, so these are exact: 21 zones of mostly-128x128 grids.
        Assert.True(totalParts >= AllZones.Length, "Every zone must produce at least one part.");
        Assert.True(totalTriangles > 1_000_000, $"Only {totalTriangles} triangles across 21 zones.");
    }

    /// <summary>
    ///     Blocked cells are the solid the walls are cut against, so a zone's open-cell count is
    ///     exactly its floor-quad count. This is what catches an off-by-one in the grid walk, which
    ///     no amount of index validation would.
    /// </summary>
    [Theory]
    [InlineData("azra")]
    [InlineData("Crypt3")]
    [InlineData("ffarena")]
    public void FloorQuadsMatchTheOpenCellCount(string zone)
    {
        var root = RequireRoot();
        var (map, prototypes) = Load(root, zone);

        var scene = ShadowkeyZoneSceneBuilder.Build(
            map, prototypes,
            options: new ShadowkeyZoneSceneOptions { IncludeCeilings = false, IncludeWalls = false });

        var open = map.Width * map.Height - map.BlockedCellCount;
        Assert.Equal(open, scene.MeshParts.Sum(part => part.Submesh.Triangles.Length / 6));
    }

    /// <summary>
    ///     The placements of a zone reach the scene through the working
    ///     <c>entities.txt</c> to <c>&lt;zone&gt;_models.txt</c> chain. On retail nothing fails to
    ///     resolve, so the assertion is sharp: azra's 282 placements all land.
    /// </summary>
    [Fact]
    public void AzraPlacementsAllResolveIntoTheScene()
    {
        var root = RequireRoot();
        var (map, prototypes) = Load(root, "azra");

        var scene = ShadowkeyZoneSceneBuilder.Build(
            map, prototypes,
            options: new ShadowkeyZoneSceneOptions { IncludeCeilings = false, IncludeWalls = false });
        var zoneParts = scene.MeshParts.Count;

        var pack = ShadowkeyModelPack.Parse(
            File.ReadAllBytes(Path.Combine(root, "models.idx")),
            File.ReadAllBytes(Path.Combine(root, "models.huge")),
            File.ReadAllText(Path.Combine(root, "models.txt")),
            "models.huge");
        var entities = ShadowkeyTextTables.ParseEntities(
            File.ReadAllBytes(Path.Combine(root, "entities.txt")), "entities.txt");
        var models = ShadowkeyTextTables.ParseModels(
            File.ReadAllBytes(Path.Combine(root, "azra_models.txt")), "azra_models.txt");
        var placements = ShadowkeyZoneFiles.ParseEnt(
            File.ReadAllBytes(Path.Combine(root, "azra.ent")), "azra.ent");

        var summary = ShadowkeyZoneSceneBuilder.AddPlacements(scene, placements, entities, models, pack);

        Assert.Equal(282, placements.Entities.Count);
        Assert.Equal(0, summary.Unresolved);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(placements.Entities.Count, summary.Placed + summary.Blank);
        Assert.True(summary.Placed > 0, "No azra placement produced geometry.");
        Assert.Equal(zoneParts + summary.Placed, scene.MeshParts.Count);

        // Every placed model registered its skin, and instances of one model share it.
        Assert.NotEmpty(scene.GeneratedTextures);
        Assert.True(
            scene.GeneratedTextures.Count <= summary.Placed,
            "A repeated model must not register its skin once per instance.");
        foreach (var part in scene.MeshParts.Skip(zoneParts))
        {
            Assert.True(
                scene.TryGetGeneratedTexture(part.Submesh.DiffuseTexturePath!, out _),
                $"{part.Name} names a texture the scene does not carry.");
        }
    }

    /// <summary>
    ///     ⚑ The mesh-unit scale, checked against a constraint from a DIFFERENT file. Mesh vertices
    ///     are bare integers with nothing in the record to say what they measure, so the divisor was
    ///     settled by the doorways: at <see cref="ShadowkeyZoneSceneBuilder.MeshUnitsPerTile" /> the
    ///     ten <c>door*</c> meshes stand 3.78 to 5.05 tiles tall, and 4.0 tiles is the standard room
    ///     height the <c>.zcp</c> ceilings carry in the large majority of records. Eight of the ten
    ///     are the same 1,036 units — 4.047 tiles, within 1.2% of that room height.
    ///     <para>
    ///         The band below is set from that measured population and is genuinely discriminating,
    ///         not merely accommodating: the rival divisors put every door outside it. At 128 the
    ///         doors would be 1.89 to 2.53 times the room height, at 512 they would be 0.47 to 0.63
    ///         — both a factor of two away and both entirely outside 0.8..1.5, which is what makes
    ///         this a test rather than a restatement.
    ///     </para>
    ///     <para>
    ///         The shortest of the ten, at 0.945, is <c>doorbarricade.bin</c> — a barricade rather
    ///         than a door, which is why the floor of the band is not drawn tight against 1.0.
    ///     </para>
    /// </summary>
    [Fact]
    public void MeshUnitsPerTile_MakesDoorsFitTheStandardRoomHeight()
    {
        var root = RequireRoot();

        var pack = ShadowkeyModelPack.Parse(
            File.ReadAllBytes(Path.Combine(root, "models.idx")),
            File.ReadAllBytes(Path.Combine(root, "models.huge")),
            File.ReadAllText(Path.Combine(root, "models.txt")),
            "models.huge");

        // The standard room: the modal .zcp ceiling height, taken from the bytes rather than
        // assumed, so this measures one file against the other.
        var ceilings = new Dictionary<short, int>();
        foreach (var record in Load(root, "azra").Prototypes.Records)
        {
            var height = record.CeilingCorners[0];
            ceilings[height] = ceilings.GetValueOrDefault(height) + 1;
        }

        var standardRoom = ceilings.MaxBy(pair => pair.Value).Key /
                           ShadowkeyZoneSceneBuilder.MeshUnitsPerTile;
        Assert.Equal(4f, standardRoom, 3);

        var heights = new List<float>();
        for (var slot = 0; slot < pack.Count; slot++)
        {
            var file = pack.Entries[slot].FileName;
            if (file is null || !file.StartsWith("door", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mesh = pack.GetMesh(slot);
            if (mesh is null)
            {
                continue;
            }

            var positions = mesh.FramePositions(0);
            var tall = (positions.Max(p => p.Y) - positions.Min(p => p.Y)) /
                       ShadowkeyZoneSceneBuilder.MeshUnitsPerTile;
            heights.Add(tall);

            Assert.InRange(tall / standardRoom, 0.8f, 1.5f);
        }

        Assert.True(heights.Count >= 8, $"Only {heights.Count} door meshes were found to measure.");

        // Sharper than the band: the height eight of the ten share is the room height itself.
        var modal = heights.GroupBy(h => h).MaxBy(group => group.Count())!;
        Assert.True(modal.Count() >= heights.Count / 2, "No single door height dominates the set.");
        Assert.Equal(1f, modal.Key / standardRoom, 1);
    }

    /// <summary>
    ///     Placements land where the <c>.ent</c> file puts them: the same tile grid the geometry is
    ///     built in, so a placement's bounds must straddle its own declared tile position rather
    ///     than sitting at the origin or a factor of 256 away.
    /// </summary>
    [Fact]
    public void PlacementsLandOnTheirDeclaredTiles()
    {
        var root = RequireRoot();
        var (map, prototypes) = Load(root, "azra");
        var scene = ShadowkeyZoneSceneBuilder.Build(
            map, prototypes,
            options: new ShadowkeyZoneSceneOptions
            {
                IncludeFloors = false, IncludeCeilings = false, IncludeWalls = false
            });

        var pack = ShadowkeyModelPack.Parse(
            File.ReadAllBytes(Path.Combine(root, "models.idx")),
            File.ReadAllBytes(Path.Combine(root, "models.huge")),
            File.ReadAllText(Path.Combine(root, "models.txt")),
            "models.huge");
        var entities = ShadowkeyTextTables.ParseEntities(
            File.ReadAllBytes(Path.Combine(root, "entities.txt")), "entities.txt");
        var models = ShadowkeyTextTables.ParseModels(
            File.ReadAllBytes(Path.Combine(root, "azra_models.txt")), "azra_models.txt");
        var placements = ShadowkeyZoneFiles.ParseEnt(
            File.ReadAllBytes(Path.Combine(root, "azra.ent")), "azra.ent");

        ShadowkeyZoneSceneBuilder.AddPlacements(scene, placements, entities, models, pack);

        var inside = 0;
        foreach (var part in scene.MeshParts)
        {
            var positions = part.Submesh.Positions;
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            for (var i = 0; i + 2 < positions.Length; i += 3)
            {
                minX = MathF.Min(minX, positions[i]);
                maxX = MathF.Max(maxX, positions[i]);
            }

            if (minX >= 0 && maxX <= map.Width)
            {
                inside++;
            }
        }

        Assert.True(
            inside > scene.MeshParts.Count * 0.9,
            $"Only {inside} of {scene.MeshParts.Count} placements sit inside the {map.Width}-tile grid.");
    }
}
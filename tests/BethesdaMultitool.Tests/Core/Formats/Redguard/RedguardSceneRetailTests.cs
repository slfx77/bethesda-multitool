using System.Numerics;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of Redguard level assembly against the Steam install:
///     every map resolves, ISLAND's counts and extents, the GUI seam, and — the part that matters —
///     the CONTROLS that settled the placement conventions, each pinned with the score of the
///     losing reading so a regression to it fails here rather than looking plausible on screen.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RedguardSceneRetailTests
{
    private static string RequireDataRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Redguard();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Redguard"));
        return root;
    }

    private static string RequireMap(string stem)
    {
        var path = Path.Combine(RequireDataRoot(), "maps", stem + ".RGM");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(stem + ".RGM"));
        return path;
    }

    [Fact]
    public void AllTwentySevenMapsResolveEveryStaticAndEveryPlacedMesh()
    {
        var root = RequireDataRoot();
        var maps = Directory.GetFiles(Path.Combine(root, "maps"), "*.RGM");
        Assert.Equal(27, maps.Length);

        var statics = 0;
        var staticsResolved = 0;
        var placements = 0;
        var placementsResolved = 0;
        var flats = 0;
        var flatsPlaced = 0;
        var terrains = 0;
        var missing = new List<string>();
        var loose = new List<string>();
        foreach (var path in maps)
        {
            using var level = RedguardLevelLoader.Load(path);
            statics += level.Scene.StaticsPlaced;
            staticsResolved += level.Scene.StaticsResolved;
            placements += level.Scene.PlacementsPlaced;
            placementsResolved += level.Scene.PlacementsResolved;
            flats += level.Flats.Placed;
            flatsPlaced += level.Flats.Instances.Count;
            terrains += level.Terrain is null ? 0 : 1;
            missing.AddRange(level.Scene.MissingNames.Select(n => $"{level.Stem}:{n}"));
            loose.AddRange(level.MeshOrigins
                .Where(o => o.Value.Source == RedguardMeshSource.LooseAnimatedKeyframe)
                .Select(o => $"{level.Stem}:{o.Key}:{o.Value.FileName}"));
            Assert.Empty(level.MeshFailures);
        }

        Assert.Equal(4_161, statics);
        Assert.Equal(statics, staticsResolved);
        Assert.Equal(1_552, placements);
        Assert.Equal(placements, placementsResolved);
        Assert.Empty(missing);

        // Measured 2026-09-28 by a read-only Python census over the raw ROB and RGM bytes
        // (tools/scripts/redguard/rob_placeholder_census.py): exactly one of the 1,552 placements
        // names an empty ROB placeholder (ISLAND MPOB #490, object TS_WAGON, mesh BWAGA001) and no
        // static does. It resolves only through the loose 3dart\BWAGA001.3DC, which is what lifts the
        // count from 1,551 to 1,552; a loader that ignores placeholders reads 1,551 here and 509 on
        // ISLAND.
        var placeholder = Assert.Single(loose);
        Assert.Equal("ISLAND:BWAGA001:BWAGA001.3DC", placeholder, ignoreCase: true);
        Assert.Equal(956, flats);
        Assert.Equal(flats, flatsPlaced);

        // ISLAND (three worlds, one terrain), NECRISLE, EXTPALAC (ISLAND.WLD), START and HIDEINT
        // (hideout.WLD) are registered outdoor maps; HIDEOUT.RGM is unregistered and takes the WLD
        // beside it. Six maps carry terrain.
        Assert.Equal(6, terrains);
    }

    [Fact]
    public void IslandAssemblesWithThePinnedCountsAndExtents()
    {
        using var level = RedguardLevelLoader.Load(RequireMap("ISLAND"));

        Assert.Equal(1_691, level.Scene.StaticsResolved);
        Assert.Equal(510, level.Scene.PlacementsResolved);
        Assert.Equal(576, level.Flats.Instances.Count);
        Assert.NotNull(level.Terrain);
        Assert.Equal("ISLAND.WLD", level.TerrainName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("island.COL", level.PaletteName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1_691 + 510 + 576 + 1, level.Instances.Count);

        // Origins of the statics and placements, in world units: the MPSO range and the MPOB/256
        // range overlap on the same island, which is the position-scale measurement itself.
        var origins = level.Scene.Instances.Select(i => Vector3.Transform(Vector3.Zero, i.Transform)).ToList();
        Assert.Equal(2_858f, origins.Min(o => o.X), 1f);
        Assert.Equal(57_774f, origins.Max(o => o.X), 1f);
        Assert.Equal(12_081f, origins.Min(o => o.Z), 1f);
        Assert.Equal(64_213f, origins.Max(o => o.Z), 1f);
        Assert.Equal(-4_144f, origins.Min(o => o.Y), 1f);
        Assert.Equal(640f, origins.Max(o => o.Y), 1f);

        // Every material the island uses resolves through island.COL and 3dart.
        var materials = level.Instances.SelectMany(i => i.Mesh.SubMeshes)
            .Select(s => (s.TextureArchive, s.TextureRecord)).Distinct().ToList();
        Assert.True(materials.Count > 300, $"expected hundreds of materials, saw {materials.Count}");
        Assert.All(materials, m => Assert.NotNull(level.Textures.Resolve(m.TextureArchive, m.TextureRecord)));
    }

    /// <summary>The lowest transformed vertex of each static, in world units.</summary>
    private static List<Vector3> LowestPoints(RedguardLevelAssembly level)
    {
        var lowest = new List<Vector3>();
        foreach (var instance in level.Scene.Instances.Where(i => i.Name.StartsWith("static_", StringComparison.Ordinal)))
        {
            Vector3? best = null;
            foreach (var vertex in instance.Mesh.SubMeshes.SelectMany(s => s.Vertices))
            {
                var world = Vector3.Transform(vertex.Position, instance.Transform);
                if (best is null || world.Y > best.Value.Y)
                {
                    best = world;
                }
            }

            if (best is not null)
            {
                lowest.Add(best.Value);
            }
        }

        return lowest;
    }

    /// <summary>The rival row mapping (<c>z &gt;&gt; 8</c> instead of <c>(65536 − z) &gt;&gt; 8</c>), bilinear like the game's.</summary>
    private static double UnflippedHeight(RedguardWldFile wld, float x, float z)
    {
        var cellX = (int)x >> 8;
        var cellZ = (int)z >> 8;
        if (x < 0 || z < 0 || cellX >= RedguardWldFile.MapSize || cellZ >= RedguardWldFile.MapSize)
        {
            return double.NaN;
        }

        var tx = ((int)x & 255) / 256.0;
        var tz = ((int)z & 255) / 256.0;
        var x1 = Math.Min(cellX + 1, RedguardWldFile.MapSize - 1);
        var z1 = Math.Min(cellZ + 1, RedguardWldFile.MapSize - 1);
        double h00 = RedguardWldFile.HeightTable[wld.HeightIndexAt(cellX, cellZ)];
        double h10 = RedguardWldFile.HeightTable[wld.HeightIndexAt(x1, cellZ)];
        double h01 = RedguardWldFile.HeightTable[wld.HeightIndexAt(cellX, z1)];
        double h11 = RedguardWldFile.HeightTable[wld.HeightIndexAt(x1, z1)];
        return -((1 - tx) * (1 - tz) * h00 + tx * (1 - tz) * h10 + (1 - tx) * tz * h01 + tx * tz * h11);
    }

    [Fact]
    public void TerrainControl_IslandStaticsStandOnTheSurface_AndTheRivalRowMappingBuriesThem()
    {
        var root = RequireDataRoot();
        using var level = RedguardLevelLoader.Load(RequireMap("ISLAND"));
        var wld = RedguardWldFile.Parse(File.ReadAllBytes(Path.Combine(root, "maps", "ISLAND.WLD")), "ISLAND.WLD");

        var lowest = LowestPoints(level);
        Assert.Equal(1_691, lowest.Count);

        var within = 0;
        var buried = 0;
        var residuals = new List<double>();
        foreach (var p in lowest)
        {
            var h = wld.SampleWorldHeight((int)p.X, (int)p.Z);
            if (double.IsNaN(h))
            {
                continue;
            }

            var r = p.Y - h;
            residuals.Add(r);
            within += Math.Abs(r) <= 64 ? 1 : 0;
            buried += r > 64 ? 1 : 0;
        }

        residuals.Sort();
        // Measured 2026-09-08: median -4.0, 1,388 within 64, 0 buried. A wrong vertical scale, a
        // wrong sign or a wrong row direction each sinks statics INTO the ground.
        Assert.True(within >= 1_380, $"{within} of {residuals.Count} lowest points within 64 units of the terrain");
        Assert.Equal(0, buried);
        Assert.InRange(residuals[residuals.Count / 2], -8.0, 0.0);

        // The nearest rival — rows running the other way — scores 157 within and buries 206.
        var rivalWithin = 0;
        var rivalBuried = 0;
        foreach (var p in lowest)
        {
            var h = UnflippedHeight(wld, p.X, p.Z);
            if (double.IsNaN(h))
            {
                continue;
            }

            rivalWithin += Math.Abs(p.Y - h) <= 64 ? 1 : 0;
            rivalBuried += p.Y - h > 64 ? 1 : 0;
        }

        Assert.True(rivalWithin <= 250, $"the unflipped mapping scored {rivalWithin} within 64 — the control no longer discriminates");
        Assert.True(rivalBuried >= 150, $"the unflipped mapping buried only {rivalBuried}");
    }

    /// <summary>Raw mesh points (not decomposed corners) of every static under one matrix reading.</summary>
    private static List<(int Owner, Vector3 Point, bool Rotated)> StaticPoints(
        RedguardRgmFile map, RedguardRobMeshArchive archive, bool transpose)
    {
        const float one = RedguardSceneAssembler.MatrixOne;
        const int serializedOne = (int)RedguardSceneAssembler.MatrixOne;
        var points = new List<(int, Vector3, bool)>();
        foreach (var s in map.StaticMeshes)
        {
            var index = archive.IndexOf(s.MeshName);
            if (index < 0 || archive.IsEmpty(index))
            {
                continue;
            }

            var mesh = archive.Parse(index);
            var m = s.Rotation;
            var rotated = !(m[0] == serializedOne && m[4] == serializedOne && m[8] == serializedOne &&
                            m[1] == 0 && m[2] == 0 && m[3] == 0 &&
                            m[5] == 0 && m[6] == 0 && m[7] == 0);
            var transform = RedguardSceneAssembler.StaticTransform(s);
            if (transpose)
            {
                var block = new Matrix4x4(
                    m[0] / one, m[1] / one, m[2] / one, 0f,
                    m[3] / one, m[4] / one, m[5] / one, 0f,
                    m[6] / one, m[7] / one, m[8] / one, 0f,
                    s.Position.X, s.Position.Y, s.Position.Z, 1f);
                transform = block;
            }

            foreach (var p in mesh.Points)
            {
                points.Add((s.Index,
                    Vector3.Transform(new Vector3(p.X, p.Y, p.Z) / XnGineMesh.PointDivisor, transform), rotated));
            }
        }

        return points;
    }

    /// <summary>Rotated statics' points that coincide (≤ 2 units) with a point of a DIFFERENT static.</summary>
    private static int SharedCorners(List<(int Owner, Vector3 Point, bool Rotated)> points)
    {
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (var i = 0; i < points.Count; i++)
        {
            var key = Cell(points[i].Point);
            if (!grid.TryGetValue(key, out var list))
            {
                list = [];
                grid[key] = list;
            }

            list.Add(i);
        }

        var shared = 0;
        for (var i = 0; i < points.Count; i++)
        {
            if (!points[i].Rotated)
            {
                continue;
            }

            var (cx, cy, cz) = Cell(points[i].Point);
            var hit = false;
            for (var dx = -1; dx <= 1 && !hit; dx++)
            {
                for (var dy = -1; dy <= 1 && !hit; dy++)
                {
                    for (var dz = -1; dz <= 1 && !hit; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                        {
                            continue;
                        }

                        foreach (var j in list)
                        {
                            if (points[j].Owner != points[i].Owner &&
                                Vector3.Distance(points[j].Point, points[i].Point) <= 2f)
                            {
                                hit = true;
                                break;
                            }
                        }
                    }
                }
            }

            shared += hit ? 1 : 0;
        }

        return shared;

        static (int, int, int) Cell(Vector3 p)
        {
            return ((int)MathF.Floor(p.X / 2f), (int)MathF.Floor(p.Y / 2f), (int)MathF.Floor(p.Z / 2f));
        }
    }

    [Fact]
    public void StaticMatrixControl_RotatedIslandStaticsShareCornersOnlyUnderTheColumnReading()
    {
        var root = RequireDataRoot();
        var map = RedguardRgmFile.Parse(File.ReadAllBytes(RequireMap("ISLAND")), "ISLAND.RGM");
        using var archive = RedguardRobMeshArchive.Open(Path.Combine(root, "3dart", "ISLAND.ROB"));

        var column = StaticPoints(map, archive, transpose: false);
        var row = StaticPoints(map, archive, transpose: true);
        Assert.Equal(47_724, column.Count(p => p.Rotated));

        var columnShared = SharedCorners(column);
        var rowShared = SharedCorners(row);

        // Measured 2026-09-08: 509 against 209. Adjacent pieces snap to shared corners in the
        // editor, so only the reading the editor used brings a rotated piece's corners onto its
        // neighbours'.
        Assert.True(columnShared >= 480, $"column reading shares {columnShared} corners");
        Assert.True(rowShared <= 260, $"row reading shares {rowShared} corners — the control no longer discriminates");
        Assert.True(columnShared > 2 * rowShared, $"column {columnShared} vs row {rowShared}");
    }

    /// <summary>Triangles of every static, binned by 64-unit grid cell for near-surface and piercing queries.</summary>
    private sealed class StaticSurface
    {
        private const float CellSize = 64f;

        /// <summary>How far past a static face both ends of an edge must lie for the edge to pierce it.</summary>
        private const float PierceEpsilon = 0.05f;
        private readonly Dictionary<(int, int, int), List<int>> _grid = [];
        private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _triangles = [];

        public StaticSurface(RedguardSceneAssembly scene)
        {
            foreach (var instance in scene.Instances.Where(i => i.Name.StartsWith("static_", StringComparison.Ordinal)))
            {
                foreach (var sub in instance.Mesh.SubMeshes)
                {
                    for (var i = 0; i + 2 < sub.Indices.Count; i += 3)
                    {
                        var a = Vector3.Transform(sub.Vertices[sub.Indices[i]].Position, instance.Transform);
                        var b = Vector3.Transform(sub.Vertices[sub.Indices[i + 1]].Position, instance.Transform);
                        var c = Vector3.Transform(sub.Vertices[sub.Indices[i + 2]].Position, instance.Transform);
                        var index = _triangles.Count;
                        _triangles.Add((a, b, c));
                        var lo = Vector3.Min(Vector3.Min(a, b), c);
                        var hi = Vector3.Max(Vector3.Max(a, b), c);
                        for (var x = Floor(lo.X); x <= Floor(hi.X); x++)
                        {
                            for (var y = Floor(lo.Y); y <= Floor(hi.Y); y++)
                            {
                                for (var z = Floor(lo.Z); z <= Floor(hi.Z); z++)
                                {
                                    if (!_grid.TryGetValue((x, y, z), out var list))
                                    {
                                        list = [];
                                        _grid[(x, y, z)] = list;
                                    }

                                    list.Add(index);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static int Floor(float v)
        {
            return (int)MathF.Floor(v / CellSize);
        }

        /// <summary>True when the point projects inside some triangle (2% slack) within <paramref name="tolerance" /> of its plane.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "S1244",
            Justification = "An exactly singular barycentric denominator cannot be divided; nearby values retain their measured behavior.")]
        public bool IsNear(Vector3 p, float tolerance)
        {
            var cx = Floor(p.X);
            var cy = Floor(p.Y);
            var cz = Floor(p.Z);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!_grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                        {
                            continue;
                        }

                        foreach (var index in list)
                        {
                            var (a, b, c) = _triangles[index];
                            var n = Vector3.Cross(b - a, c - a);
                            var length = n.Length();
                            if (length <= 0)
                            {
                                continue;
                            }

                            n /= length;
                            var d = Vector3.Dot(p - a, n);
                            if (MathF.Abs(d) > tolerance)
                            {
                                continue;
                            }

                            var proj = p - d * n;
                            var v0 = b - a;
                            var v1 = c - a;
                            var v2 = proj - a;
                            var d00 = Vector3.Dot(v0, v0);
                            var d01 = Vector3.Dot(v0, v1);
                            var d11 = Vector3.Dot(v1, v1);
                            var d20 = Vector3.Dot(v2, v0);
                            var d21 = Vector3.Dot(v2, v1);
                            var den = d00 * d11 - d01 * d01;
                            if (den == 0)
                            {
                                continue;
                            }

                            var u = (d11 * d20 - d01 * d21) / den;
                            var v = (d00 * d21 - d01 * d20) / den;
                            if (u >= -0.02f && v >= -0.02f && u + v <= 1.02f)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        ///     True when the segment's two ends lie strictly on opposite sides of some triangle's plane,
        ///     each more than <see cref="PierceEpsilon" /> from it, and the crossing point is on or inside
        ///     all three of the triangle's edges. A triangle is tested when it shares a grid cell with the
        ///     segment's bounding box.
        /// </summary>
        public bool Pierces(Vector3 p0, Vector3 p1)
        {
            var lo = Vector3.Min(p0, p1);
            var hi = Vector3.Max(p0, p1);
            for (var x = Floor(lo.X); x <= Floor(hi.X); x++)
            {
                for (var y = Floor(lo.Y); y <= Floor(hi.Y); y++)
                {
                    for (var z = Floor(lo.Z); z <= Floor(hi.Z); z++)
                    {
                        if (!_grid.TryGetValue((x, y, z), out var list))
                        {
                            continue;
                        }

                        foreach (var index in list)
                        {
                            if (Crosses(_triangles[index], p0, p1))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool Crosses((Vector3 A, Vector3 B, Vector3 C) triangle, Vector3 p0, Vector3 p1)
        {
            var (a, b, c) = triangle;
            var n = Vector3.Cross(b - a, c - a);
            var length = n.Length();
            if (length <= 0)
            {
                return false;
            }

            n /= length;
            var d0 = Vector3.Dot(p0 - a, n);
            var d1 = Vector3.Dot(p1 - a, n);
            if (!((d0 > PierceEpsilon && d1 < -PierceEpsilon) || (d0 < -PierceEpsilon && d1 > PierceEpsilon)))
            {
                return false;
            }

            var x = p0 + (p1 - p0) * (d0 / (d0 - d1));
            return Vector3.Dot(Vector3.Cross(b - a, x - a), n) >= 0 &&
                   Vector3.Dot(Vector3.Cross(c - b, x - b), n) >= 0 &&
                   Vector3.Dot(Vector3.Cross(a - c, x - c), n) >= 0;
        }
    }

    /// <summary>
    ///     The engine's matrix WITHOUT the negation it applies, Ry(+y)·Rx(+x)·Rz(+z) in column form:
    ///     the losing sign.
    /// </summary>
    private static Matrix4x4 UnnegatedRotation(RedguardRgmVector rotation)
    {
        const float scale = float.Tau / RedguardSceneAssembler.AngleUnitsPerTurn;
        return Matrix4x4.CreateRotationZ((rotation.Z & RedguardSceneAssembler.AngleMask) * scale)
               * Matrix4x4.CreateRotationX((rotation.X & RedguardSceneAssembler.AngleMask) * scale)
               * Matrix4x4.CreateRotationY((rotation.Y & RedguardSceneAssembler.AngleMask) * scale);
    }

    /// <summary>
    ///     The reading <see cref="RedguardSceneAssembler.PlacementRotation" /> used until 2026-09-28,
    ///     Ry(-y)·Rx(+x)·Rz(+z) in column form: the engine's yaw with the pitch and roll signs flipped.
    /// </summary>
    private static Matrix4x4 ReplacedPlacementRotation(RedguardRgmVector rotation)
    {
        const float scale = float.Tau / RedguardSceneAssembler.AngleUnitsPerTurn;
        return Matrix4x4.CreateRotationZ((rotation.Z & RedguardSceneAssembler.AngleMask) * scale)
               * Matrix4x4.CreateRotationX((rotation.X & RedguardSceneAssembler.AngleMask) * scale)
               * Matrix4x4.CreateRotationY(-(rotation.Y & RedguardSceneAssembler.AngleMask) * scale);
    }

    /// <summary>The unordered pairs of consecutive stored points of every plane, the closing pair included.</summary>
    private static List<(int A, int B)> Edges(XnGineMesh mesh)
    {
        var edges = new HashSet<(int, int)>();
        foreach (var plane in mesh.Planes)
        {
            for (var i = 0; i < plane.Points.Count; i++)
            {
                var a = plane.Points[i].PointIndex;
                var b = plane.Points[(i + 1) % plane.Points.Count].PointIndex;
                if (a != b)
                {
                    edges.Add((Math.Min(a, b), Math.Max(a, b)));
                }
            }
        }

        return [.. edges];
    }

    [Fact]
    public void PlacementSignControl_CatacombRotatedObjectsHugTheStaticsOnlyWithTheNegatedAngles()
    {
        var root = RequireDataRoot();
        using var level = RedguardLevelLoader.Load(RequireMap("CATACOMB"));
        using var archive = RedguardRobMeshArchive.Open(Path.Combine(root, "3dart", "CATACOMB.ROB"));
        var surface = new StaticSurface(level.Scene);

        var population = 0;
        var vertices = 0;
        var negatedNear = 0;
        var unnegatedNear = 0;
        foreach (var placement in level.Map.Placements)
        {
            if (placement.Type is not (1 or 257) || !placement.HasMesh)
            {
                continue;
            }

            var r = placement.Rotation;
            if ((r.X & 0x7FF) == 0 && (r.Y & 0x7FF) == 0 && (r.Z & 0x7FF) == 0)
            {
                continue;
            }

            var index = archive.IndexOf(placement.MeshStem);
            if (index < 0 || archive.IsEmpty(index))
            {
                continue;
            }

            var mesh = archive.Parse(index);
            if (mesh.Points.Count == 0)
            {
                continue;
            }

            population++;
            var position = new Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z) /
                           RedguardRgmFile.UnitsPerWorldUnit;
            var negated = RedguardSceneAssembler.PlacementRotation(r) * Matrix4x4.CreateTranslation(position);
            var unnegated = UnnegatedRotation(r) * Matrix4x4.CreateTranslation(position);
            foreach (var p in mesh.Points)
            {
                var local = new Vector3(p.X, p.Y, p.Z) / XnGineMesh.PointDivisor;
                vertices++;
                negatedNear += surface.IsNear(Vector3.Transform(local, negated), 6f) ? 1 : 0;
                unnegatedNear += surface.IsNear(Vector3.Transform(local, unnegated), 6f) ? 1 : 0;
            }
        }

        // Measured 2026-09-28 on the same population, 155 placements and 3,226 points, from the raw
        // CATACOMB.RGM and CATACOMB.ROB bytes by an independent Python walk that scores every reading
        // twice, in float64 and in float32 (tools/scripts/redguard/control.py). Where the two differ,
        // the figure below is the range they give. The engine's negated angles, Ry(-y)·Rx(-x)·Rz(-z),
        // put 1,016 within 6 units of a static face in both; the un-negated ones 811 to 812. Before the
        // pitch and roll fix of the same day the product read 1,013 here, this test's own count exactly,
        // against an un-negated rival of 811 to 812 as well. The 1,146 and 991 recorded here before were
        // never produced by this test: it read 1,013 at c94ac2f1, the commit that added it, and no
        // reading tried reproduces the pair (48 axis orders and sign patterns, both static matrix
        // readings, five near rules). This control sees the YAW sign only. The 133 placements that turn
        // about Y alone score 862 against 646 to 647, and every one of the 24 orders and sign patterns
        // that keeps the engine's yaw scores 991 to 1,029 in all, every one that flips it 788 to 815.
        // The other 22 (torches, levers, weights) score 129 to 168 under all 48, so the pitch and roll
        // signs and the order are not tested here; PlacementPitchRollControl tests them. The 18 CTPILL06
        // and the doors are what separate the signs.
        Assert.Equal(155, population);
        Assert.Equal(3_226, vertices);
        Assert.True(negatedNear >= 960, $"negated angles: {negatedNear} of {vertices} near a static face");
        Assert.True(unnegatedNear <= 860, $"un-negated angles: {unnegatedNear}; the control no longer discriminates");
        Assert.True(negatedNear > unnegatedNear);
    }

    [Fact]
    public void PlacementPitchRollControl_PitchedAndRolledObjectsStandInTheirRoomsOnlyWithTheEngineSigns()
    {
        var root = RequireDataRoot();
        var maps = Directory.GetFiles(Path.Combine(root, "maps"), "*.RGM");
        Assert.Equal(27, maps.Length);

        var mapsScored = 0;
        var population = 0;
        var vertices = 0;
        var edgeCount = 0;
        var engineNear = 0;
        var enginePierces = 0;
        var replacedNear = 0;
        var replacedPierces = 0;
        foreach (var path in maps)
        {
            var map = RedguardRgmFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
            var robPath = RedguardMeshLibrary.FindArchive(root, Path.GetFileNameWithoutExtension(path));
            Assert.NotNull(robPath);

            using var archive = RedguardRobMeshArchive.Open(robPath);
            var placed = new List<(RedguardRgmPlacement Placement, XnGineMesh Mesh)>();
            foreach (var placement in map.Placements)
            {
                if (placement.Type is not (1 or 257) || !placement.HasMesh)
                {
                    continue;
                }

                var r = placement.Rotation;
                if ((r.X & RedguardSceneAssembler.AngleMask) == 0 && (r.Z & RedguardSceneAssembler.AngleMask) == 0)
                {
                    continue;
                }

                var index = archive.IndexOf(placement.MeshStem);
                if (index < 0 || archive.IsEmpty(index))
                {
                    continue;
                }

                var mesh = archive.Parse(index);
                if (mesh.Points.Count > 0)
                {
                    placed.Add((placement, mesh));
                }
            }

            if (placed.Count == 0)
            {
                continue;
            }

            mapsScored++;
            using var meshes = RedguardMeshLibrary.Open(robPath, root);
            var surface = new StaticSurface(RedguardSceneAssembler.Assemble(map, meshes.Resolve));
            foreach (var (placement, mesh) in placed)
            {
                population++;
                var position = new Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z) /
                               RedguardRgmFile.UnitsPerWorldUnit;
                var engine = RedguardSceneAssembler.PlacementTransform(placement);
                var replaced = ReplacedPlacementRotation(placement.Rotation) * Matrix4x4.CreateTranslation(position);
                var local = mesh.Points.Select(p => new Vector3(p.X, p.Y, p.Z) / XnGineMesh.PointDivisor).ToList();
                var engineWorld = local.Select(p => Vector3.Transform(p, engine)).ToList();
                var replacedWorld = local.Select(p => Vector3.Transform(p, replaced)).ToList();
                vertices += local.Count;
                engineNear += engineWorld.Count(p => surface.IsNear(p, 6f));
                replacedNear += replacedWorld.Count(p => surface.IsNear(p, 6f));
                foreach (var (a, b) in Edges(mesh))
                {
                    edgeCount++;
                    enginePierces += surface.Pierces(engineWorld[a], engineWorld[b]) ? 1 : 0;
                    replacedPierces += surface.Pierces(replacedWorld[a], replacedWorld[b]) ? 1 : 0;
                }
            }
        }

        // Measured 2026-09-28 by a read-only Python walk of the raw RGM and ROB bytes that scores all 48
        // axis orders and sign patterns in float64 and in float32 (tools/scripts/redguard/multiaxis_control.py):
        // 302 placements with a pitch or a roll, on 16 maps, 5,123 points and 10,487 edges. The engine's
        // matrix, Ry(-y)·Rx(-x)·Rz(-z), puts 753 points within 6 units of a static face and pierces the
        // statics with 140 to 141 edges; the reading PlacementRotation used before that day,
        // Ry(-y)·Rx(+x)·Rz(+z), scores 647 and 290 to 291. It pierces less on 9 of the 16 maps and more on
        // none. The two thresholds together admit the engine's reading and no other of the 48: every
        // other reading within 170 pierces scores at most 719 near (Ry(-y)·Rz(-z)·Rx(-x)), and every
        // other reading with 735 near or more pierces at least 246 times (Ry(+y)·Rx(-x)·Rz(+z)). The
        // catacomb control cannot see this: its 22 such placements score 129 to 168 under all 48.
        Assert.Equal(16, mapsScored);
        Assert.Equal(302, population);
        Assert.Equal(5_123, vertices);
        Assert.Equal(10_487, edgeCount);
        Assert.True(engineNear >= 735, $"engine signs: {engineNear} of {vertices} points near a static face");
        Assert.True(enginePierces <= 170, $"engine signs: {enginePierces} of {edgeCount} edges pierce a static face");
        Assert.True(replacedNear <= 700,
            $"replaced signs: {replacedNear} points near a static face; the control no longer discriminates");
        Assert.True(replacedPierces >= 240,
            $"replaced signs: {replacedPierces} edges pierce a static face; the control no longer discriminates");
        Assert.True(engineNear > replacedNear && enginePierces < replacedPierces,
            $"engine {engineNear} near / {enginePierces} pierces against replaced {replacedNear} / {replacedPierces}");
    }

    private static AssetNode? FindNode(AssetNode node, string name)
    {
        if (node.Kind != AssetNodeKind.Folder && node.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindNode(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public void TheGuiLevelPaneOpensAMapFromTheInstallRoot_TexturedAndUnderTerrain()
    {
        var root = RequireDataRoot();
        using var session = AssetBrowseSession.TryOpenGameRoot(root);
        Assert.NotNull(session);

        var map = FindNode(session.Root, "ISLAND.RGM");
        Assert.NotNull(map);
        Assert.True(ClassicLevelPreviewSource.CanPreview(session, map));

        // The map's own mesh archive must NOT be claimed as a level: the probe is by content.
        var rob = FindNode(session.Root, "ISLAND.ROB");
        Assert.NotNull(rob);
        Assert.False(ClassicLevelPreviewSource.CanPreview(session, rob));

        var scene = ClassicLevelPreviewSource.TryLoad(session, map, CancellationToken.None);
        Assert.NotNull(scene);
        Assert.True(scene.MeshParts.Count >= 2_700, $"ISLAND placed {scene.MeshParts.Count} parts");

        var terrain = Assert.Single(scene.MeshParts, p => p.Name.StartsWith("terrain_ISLAND", StringComparison.Ordinal) &&
                                                          p.Name.EndsWith("_sub00", StringComparison.Ordinal));
        Assert.NotNull(terrain.Submesh.DiffuseTexturePath);
        Assert.True(scene.TryGetGeneratedTexture(terrain.Submesh.DiffuseTexturePath!, out var texture));
        Assert.NotNull(texture);
        Assert.Equal((64, 64), (texture.Width, texture.Height));

        var textured = scene.MeshParts.Count(p =>
            p.Submesh.DiffuseTexturePath is { } path && scene.TryGetGeneratedTexture(path, out _));
        Assert.True(textured >= scene.MeshParts.Count * 9 / 10,
            $"{textured} of {scene.MeshParts.Count} parts carry a generated texture");

        // The pane reports the approximation the CLI reports: ISLAND's wagon is the one placeholder a
        // retail map places, drawn from BWAGA001.3DC in its keyframe pose.
        var note = Assert.Single(scene.SourceNotes);
        Assert.StartsWith("1 empty ROB placeholder(s)", note, StringComparison.Ordinal);
        Assert.EndsWith(": BWAGA001.3DC", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscOneFxArtIsPreferredAndIndexParallelToTheSoftwareArt()
    {
        var root = RequireDataRoot();
        var fxArt = RealAssetPaths.Classics.RedguardDisc1FxArt();
        Assert.SkipWhen(fxArt is null, RealAssetPaths.SkipMessage("Redguard Disc 1 fxart"));

        var mapPath = RequireMap("ISLAND");
        using var software = RedguardLevelLoader.Load(File.ReadAllBytes(mapPath), "ISLAND.RGM", root);
        using var fx = RedguardLevelLoader.Load(File.ReadAllBytes(mapPath), "ISLAND.RGM", root, fxArt);
        Assert.False(software.Textures.UsesFxArt);
        Assert.True(fx.Textures.UsesFxArt);

        // 413 of the 415 TEXBSI sets carry exactly as many images as their TEXTURE twin has
        // records, so the same (archive, record) names the same picture in both renderers' art.
        var materials = fx.Scene.Instances.SelectMany(i => i.Mesh.SubMeshes)
            .Select(s => (s.TextureArchive, s.TextureRecord)).Distinct().Take(60).ToList();
        var sameSize = 0;
        foreach (var (archive, record) in materials)
        {
            var a = software.Textures.SizeOf(archive, record);
            var b = fx.Textures.SizeOf(archive, record);
            Assert.NotNull(a);
            Assert.NotNull(b);
            sameSize += a == b ? 1 : 0;
        }

        Assert.True(fx.Textures.FxArtHits >= materials.Count - 2, $"only {fx.Textures.FxArtHits} of {materials.Count} came from fxart");
        Assert.True(sameSize >= materials.Count - 2, $"{sameSize} of {materials.Count} sizes agree between TEXBSI and TEXTURE");
    }
}

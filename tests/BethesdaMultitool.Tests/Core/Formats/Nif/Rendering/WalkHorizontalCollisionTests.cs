using System.Numerics;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Focused regressions for the walk-mode XY capsule sweep. These exercise the geometry kernel used
///     by the GUI without requiring WinUI input: a wall blocks, diagonal motion slides, and a single
///     very long frame cannot step from one side of a thin wall to the other.
/// </summary>
public sealed class WalkHorizontalCollisionTests
{
    private const float Radius = 24f;
    private const float BodyMinZ = 48f;
    private const float BodyMaxZ = 120f;

    [Fact]
    public void Resolve_DirectMotionIntoWall_StopsAtCapsuleRadius()
    {
        var wall = BuildVerticalWall();

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, 0f),
            new Vector2(200f, 0f),
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [wall]);

        var final = new Vector2(-100f, 0f) + allowed;
        Assert.InRange(final.X, -24.1f, -24f);
        Assert.Equal(0f, final.Y, 3);
    }

    [Fact]
    public void Resolve_DiagonalMotionIntoWall_PreservesTangentSlide()
    {
        var wall = BuildVerticalWall();

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, -50f),
            new Vector2(200f, 100f),
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [wall]);

        var final = new Vector2(-100f, -50f) + allowed;
        Assert.InRange(final.X, -24.1f, -24f);
        Assert.InRange(final.Y, 49.8f, 50.1f);
    }

    [Fact]
    public void Resolve_HighSpeedSweep_CannotTunnelThroughThinWall()
    {
        var wall = BuildVerticalWall();

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-10_000f, 0f),
            new Vector2(20_000f, 0f),
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [wall]);

        var finalX = -10_000f + allowed.X;
        Assert.InRange(finalX, -24.1f, -24f);
    }

    [Fact]
    public void Resolve_WalkableFloor_IsLeftToVerticalGroundSampler()
    {
        var floor = BuildHorizontalFloor(60f);
        var requested = new Vector2(200f, 75f);

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, 0f),
            requested,
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [floor]);

        Assert.Equal(requested, allowed);
    }

    [Fact]
    public void Resolve_WallBelowStepHeight_DoesNotBlockGroundOwnedStep()
    {
        var lowWall = BuildVerticalWall(40f);
        var requested = new Vector2(200f, 0f);

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, 0f),
            requested,
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [lowWall]);

        Assert.Equal(requested, allowed);
    }

    [Fact]
    public void RetailCliffVertiC2_PlacedHavokSoupBlocksWarmWalkSweep()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var bsaPath = FindRetailMeshesBsa();
        Assert.SkipWhen(bsaPath is null, "Retail Fallout - Meshes.bsa is not available.");

        using var extractor = new BsaExtractor(bsaPath!);
        var file = extractor.Archive.FindFile(
            @"meshes\landscape\rocks\cliffs\cliffverti_c2.nif");
        Assert.NotNull(file);
        var data = extractor.ExtractFile(file!);
        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);
        var soup = HavokCollisionExtractor.TryExtract(data, nif!);
        Assert.NotNull(soup);
        var extracted = soup!.Value;

        // FalloutNV.esm retail authority:
        //   STAT 0x0015626B CliffVertiC2
        //   REFR 0x00168E50 in CELL 0x000DDC29 / WastelandNV grid (26,6).
        // Every one of the master's 67 CliffVertiC2 placements has an all-zero OBND, making this
        // decoded soup (not a cold bounds box) the authoritative walk surface.
        Assert.Equal(819, extracted.Positions.Length);
        Assert.Equal(4_380, extracted.Triangles.Length);
        var mesh = new CollisionMesh(extracted.Positions, extracted.Triangles);
        VectorAssert.Equal(new Vector3(-2741.1003f, -2178.751f, -1538.8652f), mesh.LocalMin, 1e-3f);
        VectorAssert.Equal(new Vector3(2964.1914f, 1103.8661f, 1148.2733f), mesh.LocalMax, 1e-3f);

        var world = PlacedReferenceTransform.ComposeWorldMatrix(
            108807.24f, 25761.17f, 5273.787f,
            0f, 0f, 3.083188f, 1f);
        var placed = WalkCollisionInstance.FromMesh(mesh, world);

        // Packed triangle 2 is a steep face at the retail placement. Start 200 units outside its
        // centroid and request a 400-unit crossing. A full move would finish 200 units behind it;
        // the 24-unit walk capsule must instead stop at the near face and never tunnel through.
        const int triangleOffset = 2 * 3;
        var a = Vector3.Transform(extracted.Positions[extracted.Triangles[triangleOffset]], world);
        var b = Vector3.Transform(extracted.Positions[extracted.Triangles[triangleOffset + 1]], world);
        var c = Vector3.Transform(extracted.Positions[extracted.Triangles[triangleOffset + 2]], world);
        var centroid = (a + b + c) / 3f;
        var faceNormal3 = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        Assert.True(MathF.Abs(faceNormal3.Z) < 0.70710677f, "Retail gate must remain a blocking face.");
        var faceNormal = Vector2.Normalize(new Vector2(faceNormal3.X, faceNormal3.Y));
        var centroidXy = new Vector2(centroid.X, centroid.Y);
        var start = centroidXy + faceNormal * 200f;
        var requested = faceNormal * -400f;
        var feetZ = centroid.Z - 84f;

        var allowed = WalkHorizontalCollision.Resolve(
            start,
            requested,
            feetZ + 48f,
            feetZ + 120f,
            Radius,
            [placed]);

        Assert.True(allowed.Length() < 200f, $"Cliff crossing was not blocked: {allowed}.");
        var finalSignedDistance = Vector2.Dot(start + allowed - centroidXy, faceNormal);
        Assert.InRange(finalSignedDistance, 23.9f, 25.2f);
    }

    [Fact]
    public void RetailNamedEffectCards_HaveNoAuthoredHavok()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var bsaPath = FindRetailMeshesBsa();
        Assert.SkipWhen(bsaPath is null, "Retail Fallout - Meshes.bsa is not available.");

        using var extractor = new BsaExtractor(bsaPath!);
        string[] paths =
        [
            @"meshes\effects\nv\nvlimestoneduststormhalfviz.nif",
            @"meshes\effects\ambient\industrial\indfxlightraysright01.nif"
        ];
        foreach (var path in paths)
        {
            var file = extractor.Archive.FindFile(path);
            Assert.NotNull(file);
            var data = extractor.ExtractFile(file!);
            var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

            Assert.Null(HavokCollisionExtractor.TryExtract(data, nif));
            Assert.False(WalkCollisionFallbackPolicy.AllowsVisualMeshFallback(path));
        }
    }

    [Fact]
    public void RetailEffectPathWithAuthoredHavok_RemainsCollisionEligible()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var bsaPath = FindRetailMeshesBsa();
        Assert.SkipWhen(bsaPath is null, "Retail Fallout - Meshes.bsa is not available.");

        using var extractor = new BsaExtractor(bsaPath!);
        const string path = @"meshes\effects\box03.nif";
        var file = extractor.Archive.FindFile(path);
        Assert.NotNull(file);
        var data = extractor.ExtractFile(file!);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var soup = HavokCollisionExtractor.TryExtract(data, nif);
        Assert.NotNull(soup);
        var extracted = soup.Value;

        Assert.Equal(16, extracted.Positions.Length);
        Assert.Equal(51, extracted.Triangles.Length);
        var collision = CollisionMeshBuilder.Build(
            path,
            PlacedObjectCategory.Effects,
            extracted.Positions,
            extracted.Triangles,
            static () => throw new InvalidOperationException(
                "Authored Havok must win before the visual fallback."));
        Assert.NotNull(collision.Mesh);
        Assert.Equal(CollisionMeshSource.AuthoredHavok, collision.Source);
    }

    [Fact]
    public void Resolve_YawRotatedPlacedBox_DoesNotWallItsUnrotatedGhostFootprint()
    {
        // A long thin cold-OBND piece (sidewalk median: 40 x 768, tall enough to wall) rotated 90°.
        // Its TRUE footprint lies along world X (y ∈ [-20, 20]); the pre-rotation-fix box walled the
        // unrotated footprint (x ∈ [-20, 20], y ∈ [-384, 384]) — a phantom wall across the road.
        var median = WalkCollisionInstance.FromPlacedBounds(
            new Vector3(-20f, -384f, 0f),
            new Vector3(20f, 384f, 200f),
            Matrix4x4.CreateRotationZ(MathF.PI / 2f));

        var requested = new Vector2(200f, 0f);
        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, 200f), requested, BodyMinZ, BodyMaxZ, Radius, [median]);

        // Walking parallel to the true box, 180 units clear of it: unobstructed.
        Assert.Equal(requested.X, allowed.X, 3);
        Assert.Equal(requested.Y, allowed.Y, 3);
    }

    [Fact]
    public void Resolve_YawRotatedPlacedBox_StillWallsItsTrueFootprint()
    {
        var median = WalkCollisionInstance.FromPlacedBounds(
            new Vector3(-20f, -384f, 0f),
            new Vector3(20f, 384f, 200f),
            Matrix4x4.CreateRotationZ(MathF.PI / 2f));

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(200f, -100f), new Vector2(0f, 200f), BodyMinZ, BodyMaxZ, Radius, [median]);

        // Approaching the true footprint edge (y = -20) head-on: stopped at capsule contact.
        var finalY = -100f + allowed.Y;
        Assert.InRange(finalY, -44.1f, -43.9f);
    }

    [Fact]
    public void Resolve_PlacedBoxBelowStepHeight_IsNeverAWall()
    {
        // A curb-height piece (top 40 < bodyMinZ 48) stays step/ground-sampler territory even when
        // cold — the broadphase Z gate must keep excluding placed boxes, exactly like mesh faces.
        var curb = WalkCollisionInstance.FromPlacedBounds(
            new Vector3(-20f, -384f, 0f),
            new Vector3(20f, 384f, 40f),
            Matrix4x4.Identity);

        var requested = new Vector2(200f, 0f);
        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, 0f), requested, BodyMinZ, BodyMaxZ, Radius, [curb]);

        Assert.Equal(requested.X, allowed.X, 3);
        Assert.Equal(requested.Y, allowed.Y, 3);
    }

    private static WalkCollisionInstance BuildVerticalWall(float topZ = 200f)
    {
        var positions = new[]
        {
            new Vector3(0f, -500f, 0f),
            new Vector3(0f, 500f, 0f),
            new Vector3(0f, 500f, topZ),
            new Vector3(0f, -500f, topZ)
        };
        var mesh = new CollisionMesh(positions, [0, 1, 2, 0, 2, 3]);
        return WalkCollisionInstance.FromMesh(mesh, Matrix4x4.Identity);
    }

    private static WalkCollisionInstance BuildHorizontalFloor(float z)
    {
        var positions = new[]
        {
            new Vector3(-500f, -500f, z),
            new Vector3(500f, -500f, z),
            new Vector3(500f, 500f, z),
            new Vector3(-500f, 500f, z)
        };
        var mesh = new CollisionMesh(positions, [0, 1, 2, 0, 2, 3]);
        return WalkCollisionInstance.FromMesh(mesh, Matrix4x4.Identity);
    }

    private static string? FindRetailMeshesBsa()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT") ?? string.Empty,
                "Fallout - Meshes.bsa"),
            Path.GetFullPath(Path.Combine("Sample", "Full_Builds", "Fallout New Vegas (PC Final)",
                "Data", "Fallout - Meshes.bsa")),
            @"E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\Fallout - Meshes.bsa"
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
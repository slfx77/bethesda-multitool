using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Collision;

/// <summary>
///     Retail-asset guard for the TES4-era Havok layout fixes (sub-shape-prefixed
///     <c>bhkPackedNiTriStripsShape</c>, 20-byte <c>TriangleData</c> stride, no Compressed flag).
///     Before those gates the Ayleid ring wall's collision cage decoded at a wrong scale/position —
///     visible from afar, gone when the camera stood next to the wall.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class OblivionHavokCollisionIntegrationTests
{
    private const string RingWallPath = @"meshes\dungeons\ayleidruins\exterior\arringouterwall01.nif";

    private static string? ResolveOblivionDataDir()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Oblivion - Meshes.bsa")))
        {
            return root;
        }

        var steam = RealAssetPaths.SteamGameDirectory("Oblivion", @"Data");
        if (steam is null)
        {
            return null;
        }

        return File.Exists(Path.Combine(steam, "Oblivion - Meshes.bsa")) ? steam : null;
    }

    [Fact]
    public void RingWall_CollisionSoupHugsTheVisualMesh()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDir = ResolveOblivionDataDir();
        Assert.SkipUnless(dataDir is not null,
            "Oblivion Data folder not found (set BETHESDA_TEST_DATA_ROOT or install Oblivion).");

        using var service = NifBrowserService.CreateFromBsa(Path.Combine(dataDir!, "Oblivion - Meshes.bsa"));
        var nifData = service.ReadNifData(RingWallPath);
        Assert.SkipUnless(nifData is not null, $"{RingWallPath} not found in Oblivion - Meshes.bsa.");

        var nif = NifParser.Parse(nifData!);
        Assert.NotNull(nif);

        var soup = HavokCollisionExtractor.Extract(nifData!, nif!).Soup;
        Assert.True(soup.HasValue, "TES4 packed collision must decode (no visual-mesh fallback).");
        Assert.True(soup!.Value.Triangles.Length >= 3);

        // Rigid export parts carry world-baked positions in the same treatRootsAsIdentity frame as
        // the collision soup, so the two AABBs are directly comparable.
        var scene = NifExportSceneBuilder.Build(nifData!, nif!, RingWallPath);
        Assert.NotNull(scene);
        var (visualMin, visualMax) = Bounds(scene!.MeshParts.SelectMany(ExtractPositions));
        var (collisionMin, collisionMax) = Bounds(soup.Value.Positions);

        var visualSize = visualMax - visualMin;
        var collisionSize = collisionMax - collisionMin;
        var visualMaxAxis = MathF.Max(visualSize.X, MathF.Max(visualSize.Y, visualSize.Z));
        var collisionMaxAxis = MathF.Max(collisionSize.X, MathF.Max(collisionSize.Y, collisionSize.Z));

        // The misparse yielded a cage floating away from the wall at the wrong scale; a correct decode
        // stays within a modest factor and shares the visual center.
        Assert.InRange(collisionMaxAxis / visualMaxAxis, 0.5f, 2f);
        var centerDelta = (collisionMin + collisionMax) * 0.5f - (visualMin + visualMax) * 0.5f;
        Assert.True(centerDelta.Length() < visualMaxAxis * 0.5f,
            $"Collision center drifts {centerDelta.Length():F1} units from the visual center " +
            $"(visual max axis {visualMaxAxis:F1}).");
    }

    private static IEnumerable<Vector3> ExtractPositions(GlbMeshPart part)
    {
        var positions = part.Submesh.Positions;
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            yield return new Vector3(positions[i], positions[i + 1], positions[i + 2]);
        }
    }

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return (min, max);
    }
}
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     VSignStairsR01 hangs flush on a Vault hall wall: 12 of its verts form a back plate at local
///     Y = 0 with authored normals (0, +1, 0) — EXACTLY coplanar with the wall face it mounts on
///     (both resolve to world Y = −1312 in Vault 34). The engine never z-fights there because the
///     plate faces INTO the wall and is backface-culled. That only holds for us if the strip→triangle
///     conversion preserves winding: a flipped triangle would survive culling and z-fight the wall.
///     This probe pins the production extraction path to emit plate triangles whose geometric winding
///     agrees with the authored +Y normals.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class VaultSignWindingProbe
{
    public VaultSignWindingProbe()
    {
        BucketBTestGuard.SkipUnlessEnabled();
    }

    [Fact]
    public void VSignStairsR01_BackPlateTriangles_WindTowardAuthoredPlusYNormal()
    {
        var nifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Unpacked_Builds\PC_Final_Unpacked\Data\meshes\dungeons\vaultruined\accessories\vsignstairsr01.nif");

        Assert.SkipWhen(nifPath is null, "FNV VSignStairsR01 NIF not available");

        var data = File.ReadAllBytes(nifPath!);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

        using var textureResolver = new NifTextureResolver();
        var model = NifGeometryExtractor.Extract(data, nif, textureResolver);

        Assert.NotNull(model);
        var sign = model.Submeshes.Single(s => s.ShapeName == "VSignStairsR01:0");
        Assert.NotNull(sign.Normals);

        Vector3 Pos(int i)
        {
            return new Vector3(sign.Positions[i * 3], sign.Positions[i * 3 + 1], sign.Positions[i * 3 + 2]);
        }

        float NormalY(int i)
        {
            return sign.Normals![i * 3 + 1];
        }

        // The back plate: every vertex at local Y == 0 whose authored normal is (0, +1, 0).
        var plate = new HashSet<int>();
        for (var i = 0; i < sign.VertexCount; i++)
        {
            if (Math.Abs(Pos(i).Y) < 0.01f && NormalY(i) > 0.99f)
            {
                plate.Add(i);
            }
        }

        Assert.True(plate.Count >= 12, $"expected the 12-vert flush back plate, found {plate.Count}");

        var plateTriangles = 0;
        for (var t = 0; t + 2 < sign.Triangles.Length; t += 3)
        {
            int a = sign.Triangles[t], b = sign.Triangles[t + 1], c = sign.Triangles[t + 2];
            if (!plate.Contains(a) || !plate.Contains(b) || !plate.Contains(c))
            {
                continue;
            }

            var geometric = Vector3.Cross(Pos(b) - Pos(a), Pos(c) - Pos(a));
            if (geometric.LengthSquared() < 1e-6f)
            {
                continue; // degenerate strip filler
            }

            plateTriangles++;
            // Winding must agree with the authored +Y normal — a flipped triangle would render
            // front-facing to the room and z-fight the coplanar wall.
            Assert.True(geometric.Y > 0f,
                $"plate triangle ({a},{b},{c}) winds against its authored +Y normal (cross.Y={geometric.Y})");
        }

        Assert.True(plateTriangles >= 4, $"expected several plate triangles, found {plateTriangles}");
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of ARCH3D.BSA against the retail ARENA2 (<c>RUN_BUCKET_B=1</c>). Every number
///     was measured with an independent Python walk of the archive (2026-09-03) before the parser
///     was written.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallArch3dRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root!;
    }

    private static DaggerfallArch3DFile OpenArchive()
    {
        var path = Path.Combine(RequireArena2(), DaggerfallArch3DFile.FileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("ARCH3D.BSA"));
        return DaggerfallArch3DFile.Open(path);
    }

    [Fact]
    public void EveryRecordParses_WithTheRetailVersionSplit()
    {
        var archive = OpenArchive();

        // Ten ids repeat, some more than twice: 14 records share another record's id.
        Assert.Equal(10_251, archive.Count);
        Assert.Equal(10_237, archive.DistinctIdCount);

        var versions = new Dictionary<XnGineMeshVersion, int>();
        var polygonMax = 0;
        var planes = 0;
        for (var i = 0; i < archive.Count; i++)
        {
            Assert.True(archive.TryParse(i, out var mesh, out var error), $"record {i}: {error}");
            versions[mesh.Version] = versions.GetValueOrDefault(mesh.Version) + 1;
            planes += mesh.Planes.Count;
            polygonMax = Math.Max(polygonMax, mesh.Planes.Max(p => p.Points.Count));
        }

        Assert.Equal(10_109, versions[XnGineMeshVersion.V27]);
        Assert.Equal(134, versions[XnGineMeshVersion.V26]);
        Assert.Equal(8, versions[XnGineMeshVersion.V25]);
        Assert.Equal(24, polygonMax);
        Assert.True(planes > 200_000, $"Expected well over 200,000 planes, saw {planes}.");
    }

    [Fact]
    public void FirstRecord_IsThePinnedMesh()
    {
        var archive = OpenArchive();

        Assert.Equal(44005u, archive.RecordId(0));
        Assert.Equal(0, archive.IndexOf(44005));
        Assert.Equal(-1, archive.IndexOf(4722));

        var mesh = archive.Parse(0);
        Assert.Equal(XnGineMeshVersion.V27, mesh.Version);
        Assert.Equal(48, mesh.Points.Count);
        Assert.Equal(34, mesh.Planes.Count);
        Assert.Equal(37_094u, mesh.Radius);
        Assert.Equal(8, mesh.ObjectDataCount);
        Assert.Equal([(24, 0), (24, 2), (24, 4), (321, 3), (321, 4)], mesh.UniqueTextures.OrderBy(t => t));

        var plane = mesh.Planes[0];
        Assert.Equal((24, 0), (plane.TextureArchive, plane.TextureRecord));
        Assert.Equal([2, 1, 0, 3], plane.Points.Select(p => p.PointIndex));
        Assert.Equal(new XnGineMeshPoint(16384, -23040, 6400), mesh.Points[2]);
        Assert.Equal((112, 512), (plane.Points[0].U, plane.Points[0].V));

        // The biggest record is a v2.6 mesh; the two "corrupt" indices of lore parse cleanly.
        var largest = archive.Parse(5552);
        Assert.Equal(451u, largest.ObjectId);
        Assert.Equal(XnGineMeshVersion.V26, largest.Version);
        Assert.Equal(934, largest.Points.Count);
        Assert.Equal(712, largest.Planes.Count);
        Assert.Equal(906u, archive.Parse(4722).ObjectId);
        Assert.Equal(907u, archive.Parse(7614).ObjectId);
    }

    [Fact]
    public void EveryRecordDecomposes_AndTheFirstExportsTextured()
    {
        var arena2 = RequireArena2();
        var archive = OpenArchive();

        var triangles = 0;
        var fanTriangles = 0;
        for (var i = 0; i < archive.Count; i++)
        {
            var parsed = archive.Parse(i);
            fanTriangles += parsed.Planes.Sum(p => Math.Max(0, p.Points.Count - 2));
            triangles += XnGineMeshDecomposer.Decompose(parsed).TriangleCount;
        }

        // Fanning every authored polygon would give more; the corner filter drops the collinear
        // points the authored polygons carry. The decomposed count is this decomposer's own pin.
        Assert.True(fanTriangles > triangles, $"Corner filtering should reduce {fanTriangles} fan triangles, saw {triangles}.");
        Assert.Equal(388_474, triangles);

        var textures = new DaggerfallMeshTextureSource(arena2);
        var mesh = XnGineMeshDecomposer.Decompose(archive.Parse(0));
        var glb = XnGineMeshGlbExporter.WriteToBytes(mesh, textures.Resolve);
        var model = ModelRoot.ParseGLB(glb);

        Assert.Equal(5, Assert.Single(model.LogicalMeshes).Primitives.Count);
        Assert.Equal(5, model.LogicalImages.Count);
    }

    [Fact]
    public async Task Analyzer_AddsAMeshRecordPerArchiveRecord()
    {
        var arena2 = RequireArena2();
        var installRoot = Path.GetDirectoryName(arena2)!;

        var result = await ClassicGameAnalyzer.LoadAsync(installRoot, TestContext.Current.CancellationToken);

        var records = result.Records.GenericRecords;
        Assert.Equal(10_251, records.Count(r => r.RecordType == DaggerfallRecordSource.MeshRecordType));
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
    }
}

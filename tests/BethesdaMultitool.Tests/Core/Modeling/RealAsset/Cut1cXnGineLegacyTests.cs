using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B for cut-1c slice 5 (plan section 8, row 5; section 9, hop A3x): the reader against the legacy extractor
///     (<see cref="XnGineMesh.Parse" /> with the legacy route's object id and layout, then
///     <see cref="XnGineMeshDecomposer.Decompose" />) per plane, triangle for triangle, over every static cover row, with
///     the plan's exclusions counted and asserted (the zero-normal three-corner planes the legacy keeps and the reader
///     omits; the Redguard UVs the legacy unfolds by accident, pinned per row) and its two controls (one moved vertex; the
///     fan apex moved to c0); the nine meshes whose texture keys empty under D5 read and convert to GLB without an empty
///     primitive; and the known Blender loss of faces that repeat a source point (slice-5 review finding 1).
/// </summary>
/// <remarks>
///     The D2 unfold gate is discriminated on retail data by two cover rows the slice-5 review added (finding 2):
///     ARCH3D 409, where the reader's unfold changes 44 stored values, and NECRISLE.ROB NCGATE01, whose planes 9, 26
///     and 61 only the legacy route unfolds. A reader that skipped the unfold below id 905 fails 409's pin (and its
///     exclusion check); one that unfolded a Redguard record fails NCGATE01's. 451 takes the unfold but changes no value,
///     so it cannot discriminate the gate on its own.
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut1cXnGineLegacyTests : IAsyncDisposable
{
    private const string Battlespire = "Builds/An Elder Scrolls Legend - Battlespire (2026-8-31, Steam - Final)/GAMEDATA/";
    private const string Redguard = "Builds/The Elder Scrolls Adventures - Redguard (2026-8-31, Steam - Final)/Redguard/3dart/";

    private readonly List<IAsyncDisposable> _owned = [];
    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-cut1c-legacy-").FullName;

    /// <summary>The static .3D rows, as (row name, payload SHA-256).</summary>
    public static TheoryData<string, string> StaticRows()
    {
        return Cut1cXnGineCover.StaticRows();
    }

    /// <summary>
    ///     The nine meshes whose texture keys empty under D5 (slice-5 receipt <c>measure_slice5.json</c>, restating the
    ///     slice-2 census): archive, directory index, entry, payload SHA-256, the emptied key, the no-triangle planes and
    ///     the primitives left. Every entry name is unique in its archive, so path lookup reaches it.
    /// </summary>
    public static TheoryData<string, int, string, string, uint, int, int> EmptiedKeyMeshes()
    {
        const string pylon1 = "08be63d672366b6329102db11d33d05d1e40d05d82c883be1a7d919010a43959";
        const string pylon2 = "f3c0ab5ddd02ba1b83e5785ada184630cc9d783e1fa6fbf4b93b6359de86861f";
        const string joust = "afa80d3de36d856eb31fb0dbe1d1211dc1d336d5f3967b6c02547680da897af6";
        return new TheoryData<string, int, string, string, uint, int, int>
        {
            { Battlespire + "3D.BSA", 509, "7PYLON1.3D", pylon1, 1078577602u, 8, 4 },
            { Battlespire + "3D.BSA", 520, "7PYLON2.3D", pylon2, 1078577602u, 8, 4 },
            { Battlespire + "3D.BSA", 522, "EBATAXE.3D", "bdce94ab2e9c52452401096f4d9f82f3d761e790ea9a48b67a9a74b11f4e7f41", 3313320004u, 10, 8 },
            { Battlespire + "3D.BSA", 2125, "EJOUST.3D", joust, 4294967294u, 12, 2 },
            { Battlespire + "3D.BS6", 506, "7PYLON1.3D", pylon1, 1078577602u, 8, 4 },
            { Battlespire + "3D.BS6", 517, "7PYLON2.3D", pylon2, 1078577602u, 8, 4 },
            { Battlespire + "3D.BS6", 1430, "ESPEAR.3D", "07b583e7df69bf9f58cbb0ac64625a07addb2685ed18947c5a44dfb4345e755e", 4294967294u, 66, 2 },
            { Battlespire + "3D.BS6", 1463, "EJOUST.3D", joust, 4294967294u, 12, 2 },
            { Redguard + "ISLAND.ROB", 258, "HBBLD01.3D", "26812c5403e4c0200f66939d133f7d565d956b2bc6dfc989569d3fd003e2561a", 172u, 1, 8 }
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless; the result stands.
        }
    }

    [Theory]
    [MemberData(nameof(StaticRows))]
    public async Task A3x_TrianglesAgreeWithTheLegacyExtractor(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var read = await Cut1cXnGineCover.ReadAsync(file, ModelNativeDetail.Metadata, _owned);
        var document = read.Result.Document;
        var header = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.HeaderKind);
        var layout = Enum.Parse<XnGineMeshLayout>(header["layout"]!.GetValue<string>());
        var stored = XnGineMesh.Parse(read.Bytes, read.LegacyObjectId, layout, uvHandling: XnGineUvHandling.Stored);
        var legacy = XnGineMesh.Parse(read.Bytes, read.LegacyObjectId, read.LegacyLayout);
        var unfolded = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UvRuleKind)["unfoldApplied"]!
            .GetValue<bool>();
        var planes = XnGineLegacyComparison.ReaderPlanes(document);

        var result = XnGineLegacyComparison.Compare(planes, legacy, stored, unfolded);

        Assert.Equal(read.LegacyLayout, layout);
        Assert.True(result.Mismatches.Count == 0,
            $"{file}: {result.Mismatches.Count} A3x difference(s) over {result.ComparedPlanes} planes: " +
            string.Join("; ", result.Mismatches.Take(10)));
        Assert.True(result.ComparedTriangles > 0, $"{file}: no triangle was compared");

        // Exclusion 1: every plane the legacy keeps and the reader omits is a zero-normal three-corner plane.
        foreach (var ordinal in result.LegacyKeptReaderOmitted)
        {
            var plane = stored.Planes[ordinal];
            Assert.True(plane.Points.Count == 3 && plane.Normal == new XnGineMeshPoint(0, 0, 0),
                $"{file}: plane {ordinal} is omitted by the reader but is not a zero-normal three-corner plane");
        }

        // Exclusion 2: UVs are left out only where the legacy parse unfolded and the reader did not, never on a
        // Daggerfall record (whose unfold below id 905 the reader applies itself: 409, 451) and never on Battlespire
        // (whose layout the unfold never reaches).
        Assert.True(result.UvExcludedPlanes.Count == 0 || (!unfolded && file.Game == "Redguard"),
            $"{file}: {result.UvExcludedPlanes.Count} UV exclusion(s) outside the Redguard accident");

        // The D2 pins (see the remarks): the stored values the reader's unfold changed, and the planes only the legacy
        // route unfolds, exactly as measured per row.
        var pins = Cut1cXnGineCover.RequirePins(file);
        Assert.Equal(pins.UnfoldedValues, XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UvRuleKind)
            ["unfoldedValues"]!.GetValue<int>());
        Assert.Equal(pins.LegacyOnlyUnfoldedPlanes, result.UvExcludedPlanes);

        // Control 1: one moved vertex is a difference.
        var first = planes.Keys.Min();
        var moved = new Dictionary<int, XnGineLegacyComparison.ReaderPlane>(planes);
        var vertices = moved[first].Vertices.ToArray();
        var touched = moved[first].Triangles[0].A;
        vertices[touched] = vertices[touched] with { Position = vertices[touched].Position + new Vector3(1, 0, 0) };
        moved[first] = moved[first] with { Vertices = vertices };
        Assert.NotEmpty(XnGineLegacyComparison.Compare(moved, legacy, stored, unfolded).Mismatches);

        // Control 2: the fan apex moved to c0 disagrees wherever an n-gon keeps c0. Three cover rows have no n-gon at
        // all (CRAK0001, MENUA001, ESPEAR: measure_slice5 cover check), where the moved apex changes nothing and the
        // control cannot discriminate; there it must change nothing, which is asserted instead.
        var ngonKeepingC0 = stored.Planes.Any(p => p.Points.Count > 3 &&
                                                   XnGineTriangulation.Triangulate(p.Index,
                                                           p.Points.Select(c => stored.Points[c.PointIndex]).ToList(),
                                                           p.Normal)
                                                       .KeptCorners.Contains(0));
        var c0Fan = XnGineLegacyComparison.Compare(XnGineLegacyComparison.FanFromCornerZero(planes), legacy, stored,
            unfolded);
        if (ngonKeepingC0)
        {
            Assert.NotEmpty(c0Fan.Mismatches);
        }
        else
        {
            Assert.DoesNotContain(stored.Planes, p => p.Points.Count > 3);
            Assert.Empty(c0Fan.Mismatches);
        }
    }

    /// <summary>
    ///     The nine emptied-key meshes (plan section 8, row 5): each reads through its container, has no empty primitive
    ///     and no material for the emptied key, lists the key and its no-triangle planes in the omitted-planes row, and
    ///     converts through the Shared GLB writer into a file whose every primitive draws triangles.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmptiedKeyMeshes))]
    public async Task EmptiedKeyMeshes_ReadAndConvert_WithoutAnEmptyPrimitive(string archive, int index, string entry,
        string sha256, uint emptiedKey, int omittedPlanes, int primitives)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = RealAssetPaths.SampleFile(archive);
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage(archive));
        var source = new BethesdaBrowseSource(
            AssetBrowseSession.TryOpenGameArchive(path) ?? AssetBrowseSession.OpenArchive(path));
        _owned.Add(source);
        var found = await ClassicContainerFixture.FindEntryAsync(source, entry);
        var item = new ModelSourceItem(source, found);
        ModelReadResult result;
        await using (var input = await item.OpenReadAsync(TestContext.Current.CancellationToken))
        {
            result = new XnGineModelReader().Read(item, new ModelReadContext(item, input,
                BethesdaModelRegistration.CreateCache()), TestContext.Current.CancellationToken);
        }

        var document = result.Document;
        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);
        Assert.Equal(sha256, document.SourceProvenance!.Sha256);
        Assert.Equal(index, XnGineModelTestSupport.Payload(document, XnGineModelNativeState.ContainerKind)
            ["entryIndex"]!.GetValue<int>());
        var mesh = Assert.Single(document.Meshes);
        Assert.Equal(primitives, mesh.Primitives.Count);
        Assert.All(mesh.Primitives, p =>
        {
            Assert.NotEmpty(p.Vertices);
            Assert.NotEmpty(p.Indices);
        });
        var omitted = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.OmittedPlanesKind);
        Assert.Equal(omittedPlanes, omitted["planes"]!.AsArray().Count);
        var emptied = Assert.Single(omitted["omittedTextureKeys"]!.AsArray());
        Assert.Equal(emptiedKey, emptied!["textureKey"]!.GetValue<uint>());
        Assert.DoesNotContain(document.Materials, m => m.Name == emptied["material"]!.GetValue<string>());
        Assert.Equal(omittedPlanes, result.Coverage.Classifications.Count(c =>
            c.ElementIdentity.StartsWith("plane:", StringComparison.Ordinal) &&
            c.Kind == ModelSourceCoverageKind.NativeOnly));

        var writer = new ModelGlbWriter();
        var environment = await writer.PreflightAsync(new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);
        var plan = writer.Plan(document, environment, new ModelConvertOptions("glb"),
            TestContext.Current.CancellationToken);
        var output = Path.Combine(_directory, Path.GetFileName(archive) + "_" + entry + ".glb");
        var written = await writer.WriteAsync(new ModelWriteRequest(plan, output), TestContext.Current.CancellationToken);

        Assert.Equal(ModelItemOutcome.Converted, written.Outcome);
        var glbPrimitives = GlbPrimitiveIndexCounts(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal(primitives, glbPrimitives.Count);
        Assert.All(glbPrimitives, count => Assert.True(count > 0 && count % 3 == 0));
    }

    /// <summary>
    ///     Slice-5 review finding 1, the known Blender loss, on every static row: the drawn planes whose corners repeat a
    ///     source point are exactly the pinned ones (3D.BSA PUZSPRT1.3D plane 72, NECRISLE.ROB NCGATE01 planes 30 and 49,
    ///     none elsewhere; ISLAND.ROB HBBLD01's plane 73 repeats one too but is omitted), <c>bmt.xngine.uv-rule</c> counts
    ///     them, the <c>bmt.xngine.repeated-point-faces</c> diagnostic appears exactly when there are any, and Shared's
    ///     Blender admission, planned without Blender, reports a Dropped <c>faces-repeat-vertex</c> row on exactly the
    ///     primitives whose faces repeat a point, counted here from <c>Faces</c> and <c>PointIndices</c> independently of
    ///     the reader. The rows with no such plane are the control: a count by position, or none at all, fails either
    ///     there or on the three pinned planes. How such planes should reach <c>Faces</c> is an open owner question; this
    ///     test pins today's loss and changes with the answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(StaticRows))]
    public async Task RepeatedPointPlanes_AreCounted_AndBlenderReportsTheirFacesDropped(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var read = await Cut1cXnGineCover.ReadAsync(file, ModelNativeDetail.Metadata, _owned);
        var document = read.Result.Document;
        var pins = Cut1cXnGineCover.RequirePins(file);

        var rule = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UvRuleKind);
        Assert.Equal(pins.RepeatedPointPlanes.Length, rule["repeatedPointPlanes"]!.GetValue<int>());
        Assert.Equal(pins.RepeatedPointPlanes,
            rule["repeatedPointPlaneOrdinals"]!.AsArray().Select(o => o!.GetValue<int>()).ToArray());
        Assert.Equal(pins.RepeatedPointPlanes.Length > 0,
            document.Diagnostics.Any(d => d.Code == XnGineModelDiagnostics.RepeatedPointFaces));

        var faces = XnGineModelTestSupport.RepeatedPointFaces(document);
        Assert.Equal(pins.RepeatedPointPlanes, faces.Values.SelectMany(planes => planes).Order().ToArray());

        var dropped = XnGineModelTestSupport.BlenderRows(document)
            .Where(row => row.ReasonCode == XnGineModelTestSupport.FacesRepeatVertex).ToList();
        Assert.All(dropped, row =>
        {
            Assert.Equal(ModelFidelityOutcome.Dropped, row.Outcome);
            Assert.Equal(SceneElementKind.Primitive, row.Target.Kind);
        });
        Assert.Equal(faces.Keys.Order().ToArray(), dropped.Select(row => row.Target.PrimitiveIndex!.Value).Order().ToArray());
    }

    /// <summary>
    ///     The index count of every primitive of every mesh in a GLB, read from its JSON chunk (12-byte header, then a
    ///     u32 length, u32 type 0x4E4F534A and the JSON), with no glTF library.
    /// </summary>
    private static List<int> GlbPrimitiveIndexCounts(byte[] glb)
    {
        Assert.Equal("glTF"u8.ToArray(), glb[..4]);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)));
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, length));
        var accessors = json.RootElement.GetProperty("accessors");
        var counts = new List<int>();
        foreach (var gltfMesh in json.RootElement.GetProperty("meshes").EnumerateArray())
        {
            foreach (var primitive in gltfMesh.GetProperty("primitives").EnumerateArray())
            {
                var indices = primitive.GetProperty("indices").GetInt32();
                counts.Add(accessors[indices].GetProperty("count").GetInt32());
            }
        }

        return counts;
    }
}

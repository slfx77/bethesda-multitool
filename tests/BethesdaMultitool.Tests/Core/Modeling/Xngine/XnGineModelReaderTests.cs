using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling.Units;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     The XnGine <c>.3D</c> reader, geometry only (cut-1c plan section 8, slice 5; sections 3 and 6), on synthetic
///     records whose every expected value is worked out by hand in the test: the document shape, the Y negation and the
///     reversed faces, the fan from the first kept corner, the split vertices with their explicit source points, the
///     stored <c>xngine.uv16</c> against the reference-rule portable UVs (and the unfold only where the reference
///     applies it), the omission of planes and texture keys that yield no triangle, the coverage census, the native
///     rows, and the declines. Each check that could pass vacuously is paired with the plan's control, which must fail.
/// </summary>
public sealed class XnGineModelReaderTests : IDisposable
{
    private static readonly Vector3 Up = new(0, 1, 0);

    private readonly ClassicContainerFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    /// <summary>
    ///     Fixture A (8-byte headers, header +20 = 1714, so Redguard by content): points p0 (0,16,0), p1 (256,16,0),
    ///     p2 (256,16,256), p3 (0,16,256), p4 (128,16,256); plane 0 key 0x0182 the pentagon (p0, p1, p2, p4, p3) whose c3
    ///     = p4 lies on the edge c2-c4; plane 1 key 0x0183 the triangle (p0, p1, p2); plane 2 key 0x0182 the triangle
    ///     (p0, p2, p3). Every authored normal is (0, -256, 0), which agrees with the stored corner order (Y down).
    ///     Plane data (24 bytes each) and 8 object-data bytes (count 2) follow the planes.
    /// </summary>
    private static XnGineTestMeshBuilder FixtureA()
    {
        var builder = new XnGineTestMeshBuilder("v2.7", 8)
        {
            HeaderPlus20 = 1714,
            Radius = 400,
            HeaderPlus36 = 0xAABBCCDD,
            HeaderPlus40 = 7,
            HeaderPlus56 = 9,
            WritePlaneData = true,
            ObjectDataCount = 2,
            ObjectData = [1, 2, 3, 4, 5, 6, 7, 8]
        };
        builder.AddPoint(0, 16, 0);
        builder.AddPoint(256, 16, 0);
        builder.AddPoint(256, 16, 256);
        builder.AddPoint(0, 16, 256);
        builder.AddPoint(128, 16, 256);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 4096, 0), (2, 0, 4096), (4, 7, 9), (3, 0, 4096)], (0, -256, 0),
            unknown1: 5, headerTail: [1, 2, 3, 4], planeData: Enumerable.Range(0, 24).Select(i => (byte)i).ToArray());
        builder.AddPlane(0x0183, [(0, 0, 0), (1, 1024, 0), (2, 0, 1024)], (0, -256, 0));
        builder.AddPlane(0x0182, [(0, 16, 32), (2, 8, 8), (3, -8, 8)], (0, -256, 0));
        return builder;
    }

    /// <summary>
    ///     Fixture B: plane 0 key 0x0182 a good triangle; plane 1 key 0x0200 three collinear points with a ZERO normal
    ///     (no area, no drawable surface); plane 2 key 0x0183 four collinear points with a non-zero normal (only c3
    ///     survives the corner test). Keys 0x0200 and 0x0183 are left with no plane that yields a triangle.
    /// </summary>
    private static byte[] FixtureB()
    {
        return FixtureBBuilder().Build();
    }

    /// <summary>Fixture B's builder (see <see cref="FixtureB" />), for a control that adds to it.</summary>
    private static XnGineTestMeshBuilder FixtureBBuilder()
    {
        var builder = new XnGineTestMeshBuilder("v2.7", 8) { HeaderPlus20 = 1714 };
        builder.AddPoint(0, 16, 0);
        builder.AddPoint(256, 16, 0);
        builder.AddPoint(256, 16, 256);
        builder.AddPoint(128, 16, 0);
        builder.AddPoint(384, 16, 0);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 64, 0), (2, 0, 64)], (0, -256, 0));
        builder.AddPlane(0x0200, [(0, 1, 2), (3, 3, 4), (1, 5, 6)], (0, 0, 0));
        builder.AddPlane(0x0183, [(0, 0, 0), (3, 0, 0), (1, 0, 0), (4, 0, 0)], (0, -256, 0));
        return builder;
    }

    // ---- Document shape and geometry (fixture A, every value by hand).

    [Fact]
    public void FixtureA_DocumentShape_UnitsBasisAndProvenance()
    {
        var bytes = FixtureA().Build();

        var result = XnGineModelTestSupport.Read(bytes);
        var document = result.Document;

        Assert.Equal("bmt.xngine.3d", document.SourceFormat);
        Assert.Equal("MODEL", document.Name);
        var scene = Assert.Single(document.Scenes);
        Assert.Equal(new[] { 0 }, scene.RootNodeIndices);
        var node = Assert.Single(document.Nodes);
        Assert.Equal(0, node.MeshIndex);
        Assert.Equal(SceneNodeRole.Transform, node.Role);
        Assert.Equal(Matrix4x4.Identity, node.LocalTransform);
        var mesh = Assert.Single(document.Meshes);
        Assert.Equal(2, mesh.Primitives.Count);
        Assert.Equal(new[] { "TEXTURE.003#2", "TEXTURE.003#3" }, document.Materials.Select(m => m.Name).ToArray());
        Assert.All(document.Materials, m =>
        {
            Assert.Null(m.Texture);
            Assert.Equal(Vector4.One, m.BaseColor);
            Assert.True(m.DoubleSided);
            Assert.False(m.Unlit);
            Assert.Equal(SceneLightingModel.MetallicRoughness, m.LightingModel);
            Assert.Equal(0f, m.MetallicFactor);
            Assert.Equal(1f, m.RoughnessFactor);
        });
        Assert.Equal(new int?[] { 0, 1 }, mesh.Primitives.Select(p => p.MaterialIndex).ToArray());

        Assert.Equal(1.0 / 20480, document.Units!.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, document.Units.Provenance);
        Assert.StartsWith("Redguard per content", document.Units.Evidence, StringComparison.Ordinal);
        Assert.Equal(Vector3.UnitY, document.SourceBasis!.Up);
        Assert.Equal(Vector3.UnitZ, document.SourceBasis.Forward);
        Assert.Equal(SceneHandedness.RightHanded, document.SourceBasis.Handedness);
        Assert.True(document.SourceBasis.NormalizedByReader);
        Assert.Equal(SceneValueProvenance.Assumed, document.SourceBasis.Provenance);
        Assert.Equal("3dart/MODEL.3D", document.SourceProvenance!.RelativePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.SourceProvenance.Sha256);
        Assert.Contains(document.Diagnostics, d => d.Code == XnGineModelDiagnostics.TextureSizeAssumed);
    }

    /// <summary>
    ///     Primitive 0 (key 0x0182): planes 0 and 2. Plane 0's face is (c0, c4, c3, c2, c1) = points (0, 3, 4, 2, 1); its
    ///     kept corners are c1, c2, c4, c0 (c3 is collinear), fanned from c1 as (1, 2, 4), (1, 4, 0) and written reversed
    ///     (1, 4, 2), (1, 0, 4), i.e. face-local vertices (4, 1, 3), (4, 0, 1). Plane 2's face is (c0, c2, c1) = points
    ///     (0, 3, 2) and its one triangle is written (0, 1, 2) of that face.
    /// </summary>
    [Fact]
    public void FixtureA_FacesTrianglesPointsAndAttributes_AreTheHandComputedOnes()
    {
        var document = XnGineModelTestSupport.Read(FixtureA().Build()).Document;
        var first = document.Meshes[0].Primitives[0];
        var second = document.Meshes[0].Primitives[1];

        Assert.Equal(new[] { 5, 3 }, first.Faces!.FaceSizes);
        Assert.Equal(Enumerable.Range(0, 8), first.Faces.CornerIndices);
        Assert.Equal(new[] { 4, 1, 3, 4, 0, 1, 5, 6, 7 }, first.Indices);
        Assert.Equal(5, first.PointIndices!.PointCount);
        Assert.Equal(new[] { 0, 3, 4, 2, 1, 0, 3, 2 }, first.PointIndices.Values);
        Assert.Equal(new[] { 0, 2 }, XnGineModelTestSupport.PlaneOrdinals(first));
        Assert.Equal(new (short, short)[] { (0, 0), (0, 4096), (7, 9), (0, 4096), (4096, 0), (16, 32), (-8, 8), (8, 8) },
            XnGineModelTestSupport.StoredUv(first));

        Assert.Equal(new[] { 3 }, second.Faces!.FaceSizes);
        Assert.Equal(new[] { 0, 1, 2 }, second.Indices);
        Assert.Equal(new[] { 0, 2, 1 }, second.PointIndices!.Values);
        Assert.Equal(new[] { 1 }, XnGineModelTestSupport.PlaneOrdinals(second));
        Assert.Equal(new (short, short)[] { (0, 0), (0, 1024), (1024, 0) }, XnGineModelTestSupport.StoredUv(second));

        // One source point domain for the whole mesh, named by the record's point list, never by its size.
        Assert.NotNull(first.PointIndices.SourceDomainId);
        Assert.Equal(first.PointIndices.SourceDomainId, second.PointIndices.SourceDomainId);
        Assert.Equal("xngine.points:memory:3dart/MODEL.3D@64", first.PointIndices.SourceDomainId);
    }

    /// <summary>
    ///     Positions are (x, -y, z), normals (nx, -ny, nz) / 256 (here (0, 1, 0)), colors white, and the portable UVs
    ///     are the reference rule / 16 / 64: plane 0's fit maps u = 16 x, v = 16 z, so its later corners take (2048,
    ///     4096) and (0, 4096) whatever they store; the triangles accumulate their deltas.
    /// </summary>
    [Fact]
    public void FixtureA_Vertices_AreNegatedAndCarryTheReferenceRuleUvs()
    {
        var document = XnGineModelTestSupport.Read(FixtureA().Build()).Document;
        var first = document.Meshes[0].Primitives[0];
        var second = document.Meshes[0].Primitives[1];

        Assert.Equal(new Vector3[]
        {
            new(0, -16, 0), new(0, -16, 256), new(128, -16, 256), new(256, -16, 256), new(256, -16, 0),
            new(0, -16, 0), new(0, -16, 256), new(256, -16, 256)
        }, first.Vertices.Select(v => v.Position).ToArray());
        Assert.All(first.Vertices.Concat(second.Vertices), v =>
        {
            Assert.Equal(Up, v.Normal);
            Assert.Equal(Vector4.One, v.Color);
        });
        Assert.Equal(new Vector2[]
        {
            new(0, 0), new(0, 4), new(2, 4), new(4, 4), new(4, 0),
            new(16f / 1024, 32f / 1024), new(16f / 1024, 48f / 1024), new(24f / 1024, 40f / 1024)
        }, first.Vertices.Select(v => v.TexCoord).ToArray());
        Assert.Equal(new Vector2[] { new(0, 0), new(1, 1), new(1, 0) }, second.Vertices.Select(v => v.TexCoord).ToArray());
        Assert.All(document.Meshes[0].Primitives, p =>
        {
            Assert.Equal(SceneNormalMode.Vertex, p.NormalMode);
            Assert.Equal(SceneNormalProvenanceKind.Authored, p.NormalProvenance!.Kind);
        });
    }

    /// <summary>
    ///     The plan's winding control: every triangle's geometric normal points along its vertices' authored normal after
    ///     the Y negation and the reversal; the unreversed winding points against it everywhere.
    /// </summary>
    [Fact]
    public void Winding_FacesOutward_AndTheUnreversedWindingDoesNot()
    {
        var document = XnGineModelTestSupport.Read(FixtureA().Build()).Document;

        var (outward, inward) = (0, 0);
        foreach (var primitive in document.Meshes[0].Primitives)
        {
            for (var t = 0; t < primitive.Indices.Count; t += 3)
            {
                var a = primitive.Vertices[primitive.Indices[t]];
                var b = primitive.Vertices[primitive.Indices[t + 1]];
                var c = primitive.Vertices[primitive.Indices[t + 2]];
                var reversed = Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal);
                var unreversed = Vector3.Dot(Vector3.Cross(c.Position - a.Position, b.Position - a.Position), a.Normal);
                outward += reversed > 0 ? 1 : 0;
                inward += unreversed > 0 ? 1 : 0;
            }
        }

        Assert.Equal(4, outward);
        Assert.Equal(0, inward);
    }

    /// <summary>
    ///     A3x on synthetic bytes: the reader agrees with the legacy extractor triangle for triangle. Controls: the fan
    ///     from c0 and one moved vertex each disagree.
    /// </summary>
    [Fact]
    public void FixtureA_AgreesWithTheLegacyExtractor_TriangleForTriangle()
    {
        var bytes = FixtureA().Build();
        var document = XnGineModelTestSupport.Read(bytes).Document;
        var legacy = XnGineMesh.Parse(bytes, 905, XnGineMeshLayout.Daggerfall);
        var stored = XnGineMesh.Parse(bytes, 905, XnGineMeshLayout.Daggerfall, uvHandling: XnGineUvHandling.Stored);
        var planes = XnGineLegacyComparison.ReaderPlanes(document);

        var result = XnGineLegacyComparison.Compare(planes, legacy, stored, readerUnfolded: false);

        Assert.Empty(result.Mismatches);
        Assert.Equal(3, result.ComparedPlanes);
        Assert.Equal(4, result.ComparedTriangles);
        Assert.Empty(result.LegacyKeptReaderOmitted);

        Assert.NotEmpty(XnGineLegacyComparison.Compare(XnGineLegacyComparison.FanFromCornerZero(planes), legacy,
            stored, readerUnfolded: false).Mismatches);
        var moved = new Dictionary<int, XnGineLegacyComparison.ReaderPlane>(planes);
        var vertices = moved[1].Vertices.ToArray();
        vertices[1] = vertices[1] with { Position = vertices[1].Position + Vector3.UnitX };
        moved[1] = moved[1] with { Vertices = vertices };
        Assert.Single(XnGineLegacyComparison.Compare(moved, legacy, stored, readerUnfolded: false).Mismatches);
    }

    /// <summary>
    ///     Two distinct source points at the same position stay two source points: the plan's welding control (a
    ///     position-keyed map merges them and so differs from the reader's).
    /// </summary>
    [Fact]
    public void CoincidentPoints_AreNotWelded()
    {
        var builder = new XnGineTestMeshBuilder("v2.7", 8) { HeaderPlus20 = 1714 };
        builder.AddPoint(0, 16, 0);
        builder.AddPoint(256, 16, 0);
        builder.AddPoint(256, 16, 256);
        builder.AddPoint(0, 16, 256);
        builder.AddPoint(0, 16, 0);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 0, 0), (2, 0, 0)], (0, -256, 0));
        builder.AddPlane(0x0182, [(4, 0, 0), (2, 0, 0), (3, 0, 0)], (0, -256, 0));

        var primitive = XnGineModelTestSupport.Read(builder.Build()).Document.Meshes[0].Primitives[0];

        Assert.Equal(new[] { 0, 2, 1, 4, 3, 2 }, primitive.PointIndices!.Values);
        Assert.Equal(primitive.Vertices[0].Position, primitive.Vertices[3].Position);
        var welded = primitive.Vertices.Select(v =>
            primitive.PointIndices.Values[primitive.Vertices.ToList().FindIndex(w => w.Position == v.Position)]).ToArray();
        Assert.NotEqual(welded, primitive.PointIndices.Values);
    }

    /// <summary>
    ///     Slice-5 review finding 1: a drawn pentagon whose corners 2 and 4 name the same point (corners p0, p1, p2, p3,
    ///     p2 with p3 = (128, 16, 384) a spike) keeps corners c1, c2, c4, c0 and draws the triangles (1, 2, 4), of zero
    ///     area, and (1, 4, 0), which covers area. It is still one face, whose point indices in face order (c0, c4, c3,
    ///     c2, c1) are 0, 2, 3, 2, 1: <c>bmt.xngine.uv-rule</c> counts it (with area), the
    ///     <c>bmt.xngine.repeated-point-faces</c> diagnostic names plane 0, and Shared's Blender admission reports the
    ///     face Dropped (<c>faces-repeat-vertex</c>), the known loss. Control: corner 4 on a distinct point p4 at p2's
    ///     position is no repeat, because identity is the point, not the position: count 0, no diagnostic, no Dropped row.
    /// </summary>
    [Fact]
    public void RepeatedSourcePoint_IsCounted_AndBlenderReportsItsFaceDropped()
    {
        var repeated = XnGineModelTestSupport.Read(SpikeRecord(lastCornerPoint: 2)).Document;
        var distinct = XnGineModelTestSupport.Read(SpikeRecord(lastCornerPoint: 4)).Document;

        var primitive = Assert.Single(repeated.Meshes[0].Primitives);
        Assert.Equal(new[] { 5 }, primitive.Faces!.FaceSizes);
        Assert.Equal(new[] { 0, 2, 3, 2, 1 }, primitive.PointIndices!.Values);
        Assert.Equal(6, primitive.Indices.Count);
        var rule = XnGineModelTestSupport.Payload(repeated, XnGineModelNativeState.UvRuleKind);
        Assert.Equal(1, rule["repeatedPointPlanes"]!.GetValue<int>());
        Assert.Equal(1, rule["repeatedPointPlanesWithArea"]!.GetValue<int>());
        Assert.Equal(new[] { 0 }, rule["repeatedPointPlaneOrdinals"]!.AsArray().Select(o => o!.GetValue<int>()).ToArray());
        Assert.Contains(repeated.Diagnostics, d => d.Code == XnGineModelDiagnostics.RepeatedPointFaces);
        Assert.Equal(new[] { 0 }, Assert.Single(XnGineModelTestSupport.RepeatedPointFaces(repeated)).Value);
        var dropped = Assert.Single(XnGineModelTestSupport.BlenderRows(repeated),
            row => row.ReasonCode == XnGineModelTestSupport.FacesRepeatVertex);
        Assert.Equal(ModelFidelityOutcome.Dropped, dropped.Outcome);
        Assert.Equal(new SceneElementRef(SceneElementKind.Primitive, 0, 0), dropped.Target);

        // Control: the same shape on a distinct fifth point.
        var control = Assert.Single(distinct.Meshes[0].Primitives);
        Assert.Equal(new[] { 0, 4, 3, 2, 1 }, control.PointIndices!.Values);
        Assert.Equal(primitive.Vertices.Select(v => v.Position), control.Vertices.Select(v => v.Position));
        var controlRule = XnGineModelTestSupport.Payload(distinct, XnGineModelNativeState.UvRuleKind);
        Assert.Equal(0, controlRule["repeatedPointPlanes"]!.GetValue<int>());
        Assert.DoesNotContain(distinct.Diagnostics, d => d.Code == XnGineModelDiagnostics.RepeatedPointFaces);
        Assert.Empty(XnGineModelTestSupport.RepeatedPointFaces(distinct));
        Assert.DoesNotContain(XnGineModelTestSupport.BlenderRows(distinct),
            row => row.ReasonCode == XnGineModelTestSupport.FacesRepeatVertex);
    }

    // ---- Stored UVs against the unfold.

    /// <summary>
    ///     The unfold applies only on a Daggerfall record whose numbered archive names an id below 905: record 451's
    ///     corner 0 stores 16000, <c>xngine.uv16</c> keeps 16000, and the portable UV takes the unfolded -384 (/1024 =
    ///     -0.375). Record 44005, the same bytes, is not unfolded (15.625). The plan's control: a uv16 filled from the
    ///     reference parse would hold -384 and fail the stored-value pin.
    /// </summary>
    [Fact]
    public async Task Unfold_OnlyBelowId905_AndUv16KeepsTheStoredValue()
    {
        var record = UnfoldRecord();
        var source = _fixture.OpenArchive(_fixture.WriteNumberedBsa((451u, record), (44005u, record)));

        var low = (await XnGineModelTestSupport.ReadEntryAsync(source, "451")).Document;
        var high = (await XnGineModelTestSupport.ReadEntryAsync(source, "44005")).Document;

        var lowPrimitive = low.Meshes[0].Primitives[0];
        Assert.Equal((short)16000, XnGineModelTestSupport.StoredUv(lowPrimitive)[0].U);
        Assert.Equal(-0.375f, lowPrimitive.Vertices[0].TexCoord.X);
        Assert.Contains(low.Diagnostics, d => d.Code == XnGineModelDiagnostics.UnfoldApplied);
        var rule = XnGineModelTestSupport.Payload(low, XnGineModelNativeState.UvRuleKind);
        Assert.True(rule["unfoldApplied"]!.GetValue<bool>());
        Assert.Equal(1, rule["unfoldedValues"]!.GetValue<int>());
        Assert.Equal("451", low.Name);

        var highPrimitive = high.Meshes[0].Primitives[0];
        Assert.Equal((short)16000, XnGineModelTestSupport.StoredUv(highPrimitive)[0].U);
        Assert.Equal(16000f / 1024, highPrimitive.Vertices[0].TexCoord.X);
        Assert.DoesNotContain(high.Diagnostics, d => d.Code == XnGineModelDiagnostics.UnfoldApplied);

        // Control: the reference parse unfolds the stored value, so a uv16 taken from it fails the pin above.
        Assert.Equal(-384, XnGineMesh.Parse(record, 451, XnGineMeshLayout.Daggerfall).Planes[0].Points[0].U);
        Assert.NotEqual(16000, XnGineMesh.Parse(record, 451, XnGineMeshLayout.Daggerfall).Planes[0].Points[0].U);
    }

    /// <summary>
    ///     A ROB segment is Redguard by its container, so the unfold never applies, although the legacy route reaches it
    ///     through the segment index 0 (the plan's "by accident"); a loose Daggerfall record without an id is not unfolded
    ///     either and says so.
    /// </summary>
    [Fact]
    public async Task Unfold_NeverOnRedguard_AndUndeterminedWithoutAnId()
    {
        var record = UnfoldRecord();
        var rob = _fixture.OpenArchive(_fixture.WriteRob(("GR_COMP", 0u, record)));

        var redguard = (await XnGineModelTestSupport.ReadEntryAsync(rob, "GR_COMP.3D")).Document;
        var loose = XnGineModelTestSupport.Read(record, XnGineModelTestSupport.Game("daggerfall")).Document;

        Assert.Equal(16000f / 1024, redguard.Meshes[0].Primitives[0].Vertices[0].TexCoord.X);
        Assert.StartsWith("Redguard per container", redguard.Units!.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(redguard.Diagnostics, d => d.Code == XnGineModelDiagnostics.UnfoldApplied);
        Assert.Equal(16000f / 1024, loose.Meshes[0].Primitives[0].Vertices[0].TexCoord.X);
        Assert.Contains(loose.Diagnostics, d => d.Code == XnGineModelDiagnostics.UnfoldUndetermined);
        Assert.Equal(ClassicModelUnits.DaggerfallMetersPerUnit, loose.Units!.MetersPerUnit);
    }

    // ---- Omissions (fixture B).

    /// <summary>
    ///     Planes 1 (zero normal, no area) and 2 (fewer than 3 kept corners) yield no triangle: they are left out of the
    ///     geometry, NativeOnly in the coverage and listed in full in <c>bmt.xngine.omitted-planes</c>, and their keys,
    ///     left with no plane, get no primitive or material.
    /// </summary>
    [Fact]
    public void NoTrianglePlanes_AreOmitted_AndTheirKeysEmptied()
    {
        var result = XnGineModelTestSupport.Read(FixtureB());
        var document = result.Document;

        var primitive = Assert.Single(document.Meshes[0].Primitives);
        Assert.Equal("TEXTURE.003#2", Assert.Single(document.Materials).Name);
        Assert.Equal(new[] { 0 }, XnGineModelTestSupport.PlaneOrdinals(primitive));
        Assert.Equal(3, primitive.Vertices.Count);
        Assert.True(NoOmittedSlotReachesTheGeometry(document, [1, 2]));

        var omitted = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.OmittedPlanesKind);
        var planes = omitted["planes"]!.AsArray();
        Assert.Equal(new[] { 1, 2 }, planes.Select(p => p!["ordinal"]!.GetValue<int>()).ToArray());
        Assert.Equal(new[] { "zero-normal-no-area", "fewer-than-3-kept-corners" },
            planes.Select(p => p!["reasonCode"]!.GetValue<string>()).ToArray());
        Assert.Equal(new[] { 0, 3, 1 }, planes[0]!["points"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray());
        Assert.Equal(new[] { 1, 3, 5 }, planes[0]!["u"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray());
        Assert.Equal(new[] { 0, 0, 0 }, planes[0]!["normal"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray());
        Assert.Equal(new[] { "TEXTURE.004#0", "TEXTURE.003#3" },
            omitted["omittedTextureKeys"]!.AsArray().Select(k => k!["material"]!.GetValue<string>()).ToArray());
        Assert.True(OmittedPlanesAgreeWithCoverage(result, omitted));

        Assert.Equal(ModelSourceCoverageKind.NativeOnly, result.Coverage.GetClassification("plane:1").Kind);
        Assert.Contains("zero authored normal and no area", result.Coverage.GetClassification("plane:1").Reason,
            StringComparison.Ordinal);
        Assert.Contains("fewer than 3 corners survive", result.Coverage.GetClassification("plane:2").Reason,
            StringComparison.Ordinal);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("plane:0").Kind);
        Assert.Contains(document.Diagnostics, d => d.Code == XnGineModelDiagnostics.OmittedPlanes);
    }

    /// <summary>
    ///     The plan's omission controls, each against the reader's own output on fixture B: keeping the zero-normal,
    ///     zero-area plane's vertices puts a zero-normal slot in the geometry (the check fails); keeping its key as a
    ///     primitive leaves an empty primitive (<c>ValidateStructure</c> fails); an omitted plane missing from the
    ///     omitted-planes row fails the cross-check; dropping one plane's classification fails the coverage count.
    /// </summary>
    [Fact]
    public void OmissionControls_EachFail()
    {
        var result = XnGineModelTestSupport.Read(FixtureB());
        var document = result.Document;
        var primitive = document.Meshes[0].Primitives[0];

        // Keeping plane 1: its three zero-normal vertices reach the geometry.
        var zeroNormal = new[] { new SceneVertex(new Vector3(0, -16, 0), Vector3.Zero, Vector4.One, Vector2.Zero) };
        var kept = new ScenePrimitive("kept", primitive.Vertices.Concat(zeroNormal), primitive.Indices, 0);
        var withKept = Rebuild(document, [kept]);
        Assert.False(NoOmittedSlotReachesTheGeometry(withKept, [1, 2]));

        // Keeping the emptied key 0x0200 as a primitive: an empty primitive fails structure validation.
        var empty = new ScenePrimitive("TEXTURE.004#0", [primitive.Vertices[0]], [], 0);
        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(Rebuild(document, [primitive, empty])));

        // An omitted plane missing from the row.
        var omitted = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.OmittedPlanesKind);
        omitted["planes"]!.AsArray().RemoveAt(1);
        Assert.False(OmittedPlanesAgreeWithCoverage(result, omitted));

        // Dropping one plane's classification: the independent census refuses the coverage.
        Assert.Throws<ArgumentException>(() => new ModelSourceCoverage(result.Coverage.Source,
            result.Coverage.CensusEvidence, result.Coverage.Elements,
            result.Coverage.Classifications.Where(c => c.ElementIdentity != "plane:2")));
    }

    /// <summary>
    ///     Slice-6 review finding 2, the <c>.3D</c> half: fixture B's p3 (128, 16, 0) and p4 (384, 16, 0) are named only by
    ///     planes that yield no triangle, and an added p5 (5, 6, 7) by no plane, so no vertex carries them. The
    ///     <c>bmt.xngine.unreferenced-points</c> row keeps their stored coordinates (with full detail its raw content is the
    ///     canonical bytes, rebuilt here from the record's own point list at header +48), and the Typed point list's
    ///     reason names the row. Control: fixture A, every point of which a drawn plane reaches, has no row and no reason.
    ///     Retail: 49 points in 9 static meshes, the cover row 3D.BS6 ESPEAR.3D among them (hop A6 compares its row).
    /// </summary>
    [Fact]
    public void UnreferencedPoints_KeepTheirStoredCoordinates_AndThePointListNamesTheRow()
    {
        var builder = FixtureBBuilder();
        builder.AddPoint(5, 6, 7);
        var bytes = builder.Build();

        var result = XnGineModelTestSupport.Read(bytes, detail: ModelNativeDetail.Full);

        var document = result.Document;
        Assert.All(document.Meshes[0].Primitives, p => Assert.Equal(new[] { 0, 2, 1 }, p.PointIndices!.Values));
        var row = Assert.Single(XnGineModelTestSupport.Rows(document, XnGineModelNativeState.UnreferencedPointsKind));
        Assert.Equal(new SceneElementRef(SceneElementKind.Mesh, 0), row.Target);
        var payload = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UnreferencedPointsKind);
        Assert.Equal(3, payload["count"]!.GetValue<int>());
        var points = payload["points"]!.AsArray();
        Assert.Equal(new[] { 3, 4, 5 }, points.Select(p => p!["point"]!.GetValue<int>()).ToArray());
        Assert.Equal(new[] { 128, 16, 0, 384, 16, 0, 5, 6, 7 },
            points.SelectMany(p => p!["position"]!.AsArray().Select(v => v!.GetValue<int>())).ToArray());

        var pointList = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(48));
        var canonical = new byte[3 * 16];
        var cursor = 0;
        foreach (var point in new[] { 3, 4, 5 })
        {
            BinaryPrimitives.WriteInt32LittleEndian(canonical.AsSpan(cursor), point);
            bytes.AsSpan(pointList + 12 * point, 12).CopyTo(canonical.AsSpan(cursor + 4));
            cursor += 16;
        }

        Assert.Equal(canonical, row.CopyRawContent());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(canonical)), payload["sha256"]!.GetValue<string>());

        var classification = result.Coverage.GetClassification("points");
        Assert.Equal(ModelSourceCoverageKind.Typed, classification.Kind);
        Assert.Equal(XnGineModelCoverage.UnreferencedPointsReason(3), classification.Reason);
        Assert.Contains(XnGineModelNativeState.UnreferencedPointsKind, classification.Reason, StringComparison.Ordinal);

        var plain = XnGineModelTestSupport.Read(FixtureA().Build(), detail: ModelNativeDetail.Full);
        Assert.Empty(XnGineModelTestSupport.Rows(plain.Document, XnGineModelNativeState.UnreferencedPointsKind));
        Assert.Null(plain.Coverage.GetClassification("points").Reason);
    }

    [Fact]
    public void EveryPlaneOmitted_IsInvalidData()
    {
        var builder = new XnGineTestMeshBuilder("v2.7", 8) { HeaderPlus20 = 1714 };
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(128, 0, 0);
        builder.AddPoint(256, 0, 0);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 0, 0), (2, 0, 0)], (0, 0, 0));

        Assert.Throws<InvalidDataException>(() => XnGineModelTestSupport.Read(builder.Build()));
    }

    // ---- Coverage and native state (fixture A).

    /// <summary>
    ///     The census from the header counts and the tiling: header [0, 64), points [64, 124), normals [124, 160), planes
    ///     [160, 272), plane data [272, 344), then the 8 object-data bytes are the one unclaimed range 344-352.
    /// </summary>
    [Fact]
    public void Coverage_IsTheIndependentCensus()
    {
        var result = XnGineModelTestSupport.Read(FixtureA().Build());

        Assert.Equal(new[]
        {
            "header", "points", "normals", "plane:0", "plane:1", "plane:2", "plane-data", "object-data",
            "unclaimed:344-352"
        }, result.Coverage.Elements.Select(e => e.Identity).ToArray());
        Assert.Equal(6, result.Coverage.TypedCount);
        Assert.Equal(3, result.Coverage.NativeOnlyCount);
        Assert.Equal(0, result.Coverage.DroppedCount);
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, result.Coverage.GetClassification("unclaimed:344-352").Kind);
    }

    /// <summary>
    ///     Slice-5 review finding 4: the object-data reason and row note follow the tiling. Fixture A's offset 344 starts
    ///     the unclaimed range 344-352, so both name that range and <c>bmt.xngine.unclaimed</c>. With header +28 moved to
    ///     200, inside the plane list [160, 272) (the shape of ARCH3D 906, whose offset lies in its plane list), they name
    ///     the plane list instead and no longer point at an unclaimed row, which holds none of those bytes (the control:
    ///     the fixed text of the first slice-5 draft would be false there).
    /// </summary>
    [Fact]
    public void ObjectDataReason_NamesWhereTheOffsetFalls()
    {
        var bytes = FixtureA().Build();
        var moved = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(moved.AsSpan(28), 200);

        var inGap = XnGineModelTestSupport.Read(bytes);
        var inPlanes = XnGineModelTestSupport.Read(moved);

        var gapReason = inGap.Coverage.GetClassification("object-data").Reason!;
        Assert.Contains("its offset 344 lies in the unclaimed range unclaimed:344-352", gapReason, StringComparison.Ordinal);
        Assert.Contains("bmt.xngine.unclaimed", gapReason, StringComparison.Ordinal);
        var gapRow = XnGineModelTestSupport.Payload(inGap.Document, XnGineModelNativeState.ObjectDataKind);
        Assert.Equal("unclaimed:344-352", gapRow["heldBy"]!.GetValue<string>());
        Assert.True(gapRow["heldByUnclaimedRange"]!.GetValue<bool>());
        Assert.Contains("bmt.xngine.unclaimed", gapRow["note"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal(new[] { "unclaimed:344-352" },
            inPlanes.Coverage.Elements.Where(e => e.Identity.StartsWith("unclaimed:", StringComparison.Ordinal))
                .Select(e => e.Identity).ToArray());
        var planesReason = inPlanes.Coverage.GetClassification("object-data").Reason!;
        Assert.Contains("its offset 200 lies inside the declared planes area [160, 272)", planesReason,
            StringComparison.Ordinal);
        Assert.DoesNotContain("bmt.xngine.unclaimed", planesReason, StringComparison.Ordinal);
        var planesRow = XnGineModelTestSupport.Payload(inPlanes.Document, XnGineModelNativeState.ObjectDataKind);
        Assert.Equal("planes", planesRow["heldBy"]!.GetValue<string>());
        Assert.False(planesRow["heldByUnclaimedRange"]!.GetValue<bool>());
        Assert.False(planesRow["startsUnclaimedRange"]!.GetValue<bool>());
        Assert.Contains("no bmt.xngine.unclaimed row holds these bytes", planesRow["note"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeRows_CarryTheHeaderPlanesAndRanges()
    {
        var bytes = FixtureA().Build();
        var document = XnGineModelTestSupport.Read(bytes).Document;

        var header = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.HeaderKind);
        Assert.Equal("v2.7", header["tag"]!.GetValue<string>());
        Assert.Equal(5, header["pointCount"]!.GetValue<int>());
        Assert.Equal(3, header["planeCount"]!.GetValue<int>());
        Assert.Equal(400u, header["radius"]!.GetValue<uint>());
        Assert.Equal(1714, header["plus20"]!.GetValue<int>());
        Assert.Equal(0xAABBCCDDu, header["plus36"]!.GetValue<uint>());
        Assert.Equal(7u, header["plus40"]!.GetValue<uint>());
        Assert.Equal(9u, header["plus56"]!.GetValue<uint>());
        Assert.Equal(272, header["planeDataOffset"]!.GetValue<int>());
        Assert.Equal(344, header["objectDataOffset"]!.GetValue<int>());
        Assert.Equal(2, header["objectDataCount"]!.GetValue<int>());
        Assert.Equal(272, header["planeListEnd"]!.GetValue<int>());
        Assert.Equal(352, header["recordLength"]!.GetValue<int>());
        Assert.Equal("Redguard", header["game"]!.GetValue<string>());
        Assert.Equal("Content", header["gameStep"]!.GetValue<string>());
        Assert.Equal("Stored", header["uvHandling"]!.GetValue<string>());
        Assert.Null(header["objectId"]);

        var planes = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.PlanesKind);
        var plane0 = planes["planes"]!.AsArray()[0]!;
        Assert.Equal(5, plane0["unknown1"]!.GetValue<int>());
        Assert.Equal("01020304", plane0["headerTail"]!.GetValue<string>());
        Assert.Equal(0x0182u, plane0["textureKey"]!.GetValue<uint>());

        var planeData = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.PlaneDataKind);
        Assert.Equal(272, planeData["start"]!.GetValue<int>());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(272, 72))),
            planeData["sha256"]!.GetValue<string>());
        Assert.False(Assert.Single(XnGineModelTestSupport.Rows(document, XnGineModelNativeState.PlaneDataKind))
            .HasRawContent);
        Assert.True(XnGineModelTestSupport.Payload(document, XnGineModelNativeState.ObjectDataKind)
            ["startsUnclaimedRange"]!.GetValue<bool>());
        var rule = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UvRuleKind);
        Assert.Equal(1, rule["collinearCornersDropped"]!.GetValue<int>());
        Assert.Equal(4, rule["triangles"]!.GetValue<int>());
        Assert.False(rule["unfoldApplied"]!.GetValue<bool>());
        Assert.Empty(XnGineModelTestSupport.Rows(document, XnGineModelNativeState.OmittedPlanesKind));
        Assert.Empty(XnGineModelTestSupport.Rows(document, XnGineModelNativeState.ContainerKind));

        // Full detail keeps the raw ranges; the plane data is the 24 bytes 0..23 then two zero records.
        var full = XnGineModelTestSupport.Read(bytes, detail: ModelNativeDetail.Full).Document;
        var raw = Assert.Single(XnGineModelTestSupport.Rows(full, XnGineModelNativeState.PlaneDataKind));
        Assert.True(raw.HasRawContent);
        Assert.Equal(bytes.AsSpan(272, 72).ToArray(), raw.CopyRawContent());
        Assert.Equal(Enumerable.Range(0, 24).Select(i => (byte)i), raw.CopyRawContent().Take(24));
    }

    /// <summary>
    ///     A named archive's LZSS entry is Battlespire by its container: the document hashes the decoded record, and the
    ///     container row pins the stored bytes (their SHA-256 differs); the point domain names the container record.
    /// </summary>
    [Fact]
    public async Task LzssEntry_PinsTheStoredBytes_InTheContainerRow()
    {
        var record = XnGineTestMeshBuilder.TenByteRecord();
        var source = _fixture.OpenArchive(_fixture.WriteNamedBsa(("HUTVANE.3D", record, true)));

        var document = (await XnGineModelTestSupport.ReadEntryAsync(source, "HUTVANE.3D")).Document;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(record)), document.SourceProvenance!.Sha256);
        var container = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.ContainerKind);
        Assert.True(container["compressed"]!.GetValue<bool>());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(ClassicContainerFixture.LzssLiteral(record))),
            container["storedSha256"]!.GetValue<string>());
        Assert.NotEqual(document.SourceProvenance.Sha256, container["storedSha256"]!.GetValue<string>());
        Assert.Equal(ClassicModelUnits.BattlespireMetersPerUnit, document.Units!.MetersPerUnit);
        Assert.StartsWith("xngine.points:NamedXnGineBsa:",
            document.Meshes[0].Primitives[0].PointIndices!.SourceDomainId, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two byte-identical records in one numbered archive get distinct point domains (their identity is the record,
    ///     never the equal sizes or positions): the Shared SA6 control.
    /// </summary>
    [Fact]
    public async Task ByteEqualRecords_GetDistinctPointDomains()
    {
        var record = UnfoldRecord();
        var source = _fixture.OpenArchive(_fixture.WriteNumberedBsa((44004u, record), (44005u, record)));

        var a = (await XnGineModelTestSupport.ReadEntryAsync(source, "44004")).Document;
        var b = (await XnGineModelTestSupport.ReadEntryAsync(source, "44005")).Document;

        var domainA = a.Meshes[0].Primitives[0].PointIndices!.SourceDomainId;
        var domainB = b.Meshes[0].Primitives[0].PointIndices!.SourceDomainId;
        Assert.NotEqual(domainA, domainB);
        Assert.Contains("#0:44004@", domainA, StringComparison.Ordinal);
        Assert.Contains("#1:44005@", domainB, StringComparison.Ordinal);
    }

    // ---- Declines, inspection and the game.

    [Fact]
    public void AnimatedShape_ThreeDFxAndStrays_AreDeclined()
    {
        Assert.Throws<NotSupportedException>(() =>
            XnGineModelTestSupport.Read(XnGineModelTestSupport.AnimatedShapeRecord()));
        var fxart = XnGineTestMeshBuilder.EightByteRecord(headerPlus20: 1714);
        "v5.0"u8.CopyTo(fxart);
        Assert.Equal(XnGineModelFormatMetadata.FxartUnsupportedReason,
            Assert.Throws<NotSupportedException>(() => XnGineModelTestSupport.Read(fxart)).Message);
        var mz = new byte[512];
        "MZ"u8.CopyTo(mz);
        Assert.Throws<InvalidDataException>(() => XnGineModelTestSupport.Read(mz));

        // Control: the animated-shape bytes with +16 = 0 read as a static mesh.
        Assert.Single(XnGineModelTestSupport.Read(XnGineModelTestSupport.AnimatedShapeRecord(frames: 0)).Document.Meshes);
    }

    /// <summary>
    ///     Inspection reads what conversion reads (no stage decodes a pixel; slice 5 carries no image), and a game the
    ///     bytes cannot belong to throws: <c>--game battlespire</c> on an 8-byte-only record.
    /// </summary>
    [Fact]
    public void Inspection_ReadsTheSameDocument_AndAConflictingGameThrows()
    {
        var bytes = FixtureA().Build();
        var conversion = XnGineModelTestSupport.Read(bytes).Document;
        var inspection = XnGineModelTestSupport.Read(bytes, purpose: ModelReadPurpose.Inspection).Document;

        Assert.True(new XnGineModelReader().SupportsInspectionWithoutPixelDecoding);
        Assert.Equal(conversion.Meshes[0].Primitives.Select(p => p.Indices.Count),
            inspection.Meshes[0].Primitives.Select(p => p.Indices.Count));
        Assert.Empty(inspection.Images);
        Assert.Throws<ArgumentException>(() =>
            XnGineModelTestSupport.Read(bytes, XnGineModelTestSupport.Game("battlespire")));
        Assert.Equal(BethesdaGame.Daggerfall.ToString(), XnGineModelTestSupport.Payload(
                XnGineModelTestSupport.Read(bytes, XnGineModelTestSupport.Game("daggerfall")).Document,
                XnGineModelNativeState.HeaderKind)["game"]!.GetValue<string>());
    }

    /// <summary>A 3-corner record whose corner 0 stores the fold-range value 16000 (8-byte headers, header +20 = 0).</summary>
    /// <summary>
    ///     The spike pentagon of <see cref="RepeatedSourcePoint_IsCounted_AndBlenderReportsItsFaceDropped" />: points p0
    ///     (0,16,0), p1 (256,16,0), p2 (256,16,256), p3 (128,16,384) and p4 (256,16,256), one plane keyed 0x0182 over p0,
    ///     p1, p2, p3 and the point <paramref name="lastCornerPoint" /> (2 repeats p2; 4 is p2's twin position).
    /// </summary>
    private static byte[] SpikeRecord(int lastCornerPoint)
    {
        var builder = new XnGineTestMeshBuilder("v2.7", 8) { HeaderPlus20 = 1714 };
        builder.AddPoint(0, 16, 0);
        builder.AddPoint(256, 16, 0);
        builder.AddPoint(256, 16, 256);
        builder.AddPoint(128, 16, 384);
        builder.AddPoint(256, 16, 256);
        builder.AddPlane(0x0182, [(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (lastCornerPoint, 0, 0)], (0, -256, 0));
        return builder.Build();
    }

    private static byte[] UnfoldRecord()
    {
        var builder = new XnGineTestMeshBuilder("v2.7", 8);
        builder.AddPoint(0, 0, 0);
        builder.AddPoint(256, 0, 0);
        builder.AddPoint(0, 0, 256);
        builder.AddPlane(0x0182, [(0, 16000, 0), (1, 0, 0), (2, 0, 0)], (0, -256, 0));
        return builder.Build();
    }

    /// <summary>
    ///     The check the omission controls run: no vertex with a zero normal reaches the geometry, and no face carries an
    ///     omitted plane.
    /// </summary>
    private static bool NoOmittedSlotReachesTheGeometry(ModelDocument document, int[] omittedPlanes)
    {
        foreach (var primitive in document.Meshes[0].Primitives)
        {
            if (primitive.Vertices.Any(v => v.Normal == Vector3.Zero))
            {
                return false;
            }

            if (primitive.Attributes.Any(a => a.Name == XnGineModelGeometry.PlaneAttributeName) &&
                XnGineModelTestSupport.PlaneOrdinals(primitive).Intersect(omittedPlanes).Any())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The omitted-planes row lists exactly the planes the coverage classifies NativeOnly.</summary>
    private static bool OmittedPlanesAgreeWithCoverage(ModelReadResult result, System.Text.Json.Nodes.JsonObject row)
    {
        var listed = row["planes"]!.AsArray().Select(p => "plane:" + p!["ordinal"]!.GetValue<int>()).ToHashSet();
        var nativeOnly = result.Coverage.Classifications
            .Where(c => c.ElementIdentity.StartsWith("plane:", StringComparison.Ordinal) &&
                        c.Kind == ModelSourceCoverageKind.NativeOnly)
            .Select(c => c.ElementIdentity).ToHashSet();
        return listed.SetEquals(nativeOnly);
    }

    /// <summary>The document with its one mesh's primitives replaced (for a control).</summary>
    private static ModelDocument Rebuild(ModelDocument document, IReadOnlyList<ScenePrimitive> primitives)
    {
        return new ModelDocument(document.SourceFormat, document.Name, document.Scenes, document.Nodes,
            [new SceneMesh(document.Meshes[0].Name, primitives)], document.Materials, units: document.Units,
            sourceBasis: document.SourceBasis);
    }
}

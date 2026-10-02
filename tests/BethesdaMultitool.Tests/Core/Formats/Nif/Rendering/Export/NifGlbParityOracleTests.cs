using System.Numerics;
using System.Reflection;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;
using Xunit;
using Xunit.Sdk;
using ToolkitScene = SharpGLTF.Scenes.SceneBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Adversarial cases for the v2 parity oracle (plan section 4). Every passing case has a paired control that
///     changes exactly the property under test and must fail, so no rule here can pass vacuously.
/// </summary>
/// <remarks>
///     The repeated-position, whole-degenerate-part, missing-triangle, name and source-parity cases run the real
///     writers: the normalized plan through <c>NifGlbExport.Plan</c> with a test admission and the native
///     <c>GlbWriter</c> afterwards. The encoding cases build shared documents directly so a single attribute can be
///     varied while everything else stays byte-identical.
/// </remarks>
public sealed class NifGlbParityOracleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The exact source count R passes; any other declared R fails even though geometry still pairs.</summary>
    [Fact]
    public void RepeatedPositions_ExactCountPasses_AndAnyOtherCountFails()
    {
        using var resolver = Resolver();
        var pair = NifGlbParityCorpus.EncodePair(Scene(("surface", false, [Triangle(0f), RepeatedTriangle(2f)])),
            resolver, Request(), Token);
        var native = ModelRoot.ParseGLB(pair.NativeBytes);
        var shared = ModelRoot.ParseGLB(pair.SharedBytes);

        // Independently known: the fixture holds exactly one repeated-position triangle, which only the
        // toolkit drops.
        Assert.Equal(1, pair.RepeatedPositions);
        Assert.Equal(1, TriangleCount(native));
        Assert.Equal(2, TriangleCount(shared));

        var exact = NifGlbParityOracle.Compare(native, shared, pair.Expectations, Token);
        Assert.True(exact.Passed, exact.Summary());
        Assert.Equal(1, exact.RemovedSharedTriangles);
        Assert.Equal(1, exact.FeatureClasses[NifGlbParityOracle.RepeatedPositionDrop]);

        foreach (var declared in new int?[] { 0, 2 })
        {
            var wrong = NifGlbParityOracle.Compare(native, shared,
                pair.Expectations with { RepeatedPositionTriangles = declared }, Token);
            Assert.False(wrong.Passed);
            Assert.Contains(wrong.Failures, static failure => failure.Contains("exactly repeated positions",
                StringComparison.Ordinal));
        }

        var unaccounted = NifGlbParityOracle.Compare(native, shared,
            pair.Expectations with { RepeatedPositionTriangles = null }, Token);
        Assert.False(unaccounted.Passed);
        Assert.Equal(1, unaccounted.UnmatchedSharedTriangles);
    }

    /// <summary>
    ///     A missing distinct-position triangle fails even when the normalized GLB holds exactly R repeated-position
    ///     triangles, so the removal can never hide a real loss.
    /// </summary>
    [Fact]
    public void MissingDistinctTriangle_FailsEvenWithExactRepeatedCount()
    {
        using var resolver = Resolver();
        var complete = NifGlbParityCorpus.EncodePair(
            Scene(("surface", false, [Triangle(0f), Triangle(2f), RepeatedTriangle(4f)])), resolver, Request(),
            Token);
        var control = NifGlbParityOracle.Compare(ModelRoot.ParseGLB(complete.NativeBytes),
            ModelRoot.ParseGLB(complete.SharedBytes), complete.Expectations, Token);
        Assert.True(control.Passed, control.Summary());

        var lossy = NifGlbParityCorpus.EncodePair(
            Scene(("surface", false, [Triangle(0f), RepeatedTriangle(4f)])), resolver, Request(), Token);
        var report = NifGlbParityOracle.Compare(ModelRoot.ParseGLB(complete.NativeBytes),
            ModelRoot.ParseGLB(lossy.SharedBytes), complete.Expectations, Token);

        Assert.False(report.Passed);
        Assert.Equal(1, report.RemovedSharedTriangles);
        Assert.Equal(1, report.UnmatchedNativeTriangles);
        Assert.Equal(0, report.UnmatchedSharedTriangles);
        Assert.DoesNotContain(report.Failures, static failure => failure.Contains("exactly repeated positions",
            StringComparison.Ordinal));
    }

    /// <summary>
    ///     A part made only of repeated-position triangles vanishes from the native GLB with its material; the
    ///     normalized GLB must keep that material, and the accounting excludes it from the native expectation.
    /// </summary>
    [Fact]
    public void WholeDegeneratePart_ItsMaterialIsRequiredOnlyInTheNormalizedGlb()
    {
        using var resolver = Resolver();
        var pair = NifGlbParityCorpus.EncodePair(
            Scene(("surface", false, [Triangle(0f)]), ("sliver", true, [RepeatedTriangle(3f)])), resolver,
            Request(), Token);
        var native = ModelRoot.ParseGLB(pair.NativeBytes);
        var shared = ModelRoot.ParseGLB(pair.SharedBytes);

        // Measured on SharpGLTF.Toolkit 1.0.6: a mesh whose only triangle is rejected is empty, so the writer
        // skips it and never emits its material. The normalized writer keeps both parts and both materials.
        Assert.Single(native.LogicalMaterials);
        Assert.Equal(2, shared.LogicalMaterials.Count);
        Assert.Equal(1, pair.RepeatedPositions);

        var report = NifGlbParityOracle.Compare(native, shared, pair.Expectations, Token);
        Assert.True(report.Passed, report.Summary());
        Assert.Equal(1, report.RemovedOnlyMaterialSignatures);

        var unaccounted = NifGlbParityOracle.Compare(native, shared,
            pair.Expectations with { RepeatedPositionTriangles = null }, Token);
        Assert.False(unaccounted.Passed);
        Assert.Contains(unaccounted.Failures, static failure => failure.Contains("has no native row",
            StringComparison.Ordinal));
    }

    /// <summary>Every native mesh name must appear among the normalized mesh or primitive names.</summary>
    [Fact]
    public void NativeMeshName_MustAppearAmongNormalizedMeshOrPrimitiveNames()
    {
        using var resolver = Resolver();
        var matching = NifGlbParityCorpus.EncodePair(Scene(("alpha", false, [Triangle(0f)])), resolver, Request(),
            Token);
        var native = ModelRoot.ParseGLB(matching.NativeBytes);
        var control = NifGlbParityOracle.Compare(native, ModelRoot.ParseGLB(matching.SharedBytes),
            matching.Expectations, Token);
        Assert.True(control.Passed, control.Summary());
        Assert.Equal("alpha", Assert.Single(native.LogicalMeshes).Name);

        var renamed = NifGlbParityCorpus.EncodePair(Scene(("beta", false, [Triangle(0f)])), resolver, Request(),
            Token);
        var report = NifGlbParityOracle.Compare(native, ModelRoot.ParseGLB(renamed.SharedBytes),
            renamed.Expectations, Token);
        Assert.Equal(1, report.FailureCount);
        Assert.Contains("Native mesh name 'alpha'", Assert.Single(report.Failures), StringComparison.Ordinal);
    }

    /// <summary>Every native material name must appear among the normalized material names.</summary>
    [Fact]
    public void NativeMaterialName_MustAppearAmongNormalizedMaterialNames()
    {
        var control = NifGlbParityOracle.Compare(Encode(materialName: "matte"), Encode(materialName: "matte"),
            NifGlbParityExpectations.Strict, Token);
        Assert.True(control.Passed, control.Summary());

        var report = NifGlbParityOracle.Compare(Encode(materialName: "matte"), Encode(materialName: "gloss"),
            NifGlbParityExpectations.Strict, Token);
        Assert.Equal(1, report.FailureCount);
        Assert.Contains("Native material name 'matte'", Assert.Single(report.Failures), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Pins the measured native encoder: SharpGLTF.Toolkit 1.0.6 stores COLOR_0 as normalized unsigned bytes and
    ///     truncates. 0.999, 0.5 and 0.2 scale to 254.745, 127.5 and 51.0; rounding would give 255 and 128.
    /// </summary>
    [Fact]
    public void NativeToolkitEncoder_WritesTruncatedNormalizedUnsignedBytes()
    {
        var material = new MaterialBuilder("material");
        var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexEmpty>("mesh");
        var color = new Vector4(0.999f, 0.5f, 0.2f, 1f);
        mesh.UsePrimitive(material).AddTriangle(ToolkitVertex(Vector3.Zero, color),
            ToolkitVertex(Vector3.UnitX, color), ToolkitVertex(Vector3.UnitY, color));
        var scene = new ToolkitScene();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        using var stream = new MemoryStream();
        scene.ToGltf2().WriteGLB(stream);
        var accessor = ModelRoot.ParseGLB(stream.ToArray()).LogicalMeshes[0].Primitives[0]
            .GetVertexAccessor("COLOR_0");

        Assert.Equal(EncodingType.UNSIGNED_BYTE, accessor.Encoding);
        Assert.True(accessor.Normalized);
        Assert.All(accessor.AsVector4Array(), static decoded =>
            Assert.Equal(new Vector4(254f, 127f, 51f, 255f),
                new Vector4(MathF.Round(decoded.X * 255f), MathF.Round(decoded.Y * 255f),
                    MathF.Round(decoded.Z * 255f), MathF.Round(decoded.W * 255f))));
    }

    /// <summary>
    ///     A truncated native byte agrees with the float it came from even when the two differ by more than half a
    ///     step; the same byte read as rounded, or a byte one step lower, disagrees.
    /// </summary>
    [Fact]
    public void ColorEncoding_ToleranceFollowsTheEncoderRule()
    {
        var truncated = new Vector4(254f / 255f, 1f, 1f, 1f);
        var source = new Vector4(0.999f, 1f, 1f, 1f);
        var native = Encode(color: truncated, encoding: SceneColorEncoding.UnsignedByteNormalized);
        var shared = Encode(color: source);

        // 0.999 - 254/255 is about 0.0029, larger than the 0.5/255 a rounding encoder would allow.
        Assert.True(source.X - truncated.X > 0.5f / 255f);
        var report = NifGlbParityOracle.Compare(native, shared, NifGlbParityExpectations.Strict, Token);
        Assert.True(report.Passed, report.Summary());
        Assert.Equal(1, report.NativeColorAccessors["UNSIGNED_BYTE normalized"]);
        Assert.Equal(1, report.SharedColorAccessors["FLOAT"]);

        var asRounded = NifGlbParityOracle.Compare(native, shared,
            NifGlbParityExpectations.Strict with { NativeColorQuantization = NifGlbColorQuantization.RoundToNearest },
            Token);
        Assert.False(asRounded.Passed);

        var oneStepLow = Encode(color: new Vector4(253f / 255f, 1f, 1f, 1f),
            encoding: SceneColorEncoding.UnsignedByteNormalized);
        Assert.False(NifGlbParityOracle.Compare(oneStepLow, shared, NifGlbParityExpectations.Strict, Token).Passed);
    }

    /// <summary>World tolerance grows with distance: 0.05 passes at 100,000 units and fails near the origin.</summary>
    [Fact]
    public void WorldTolerance_IsRelativeToTheCoordinate()
    {
        var far = NifGlbParityOracle.Compare(Encode(placement: Matrix4x4.CreateTranslation(100000f, 0f, 0f)),
            Encode(placement: Matrix4x4.CreateTranslation(100000.05f, 0f, 0f)), NifGlbParityExpectations.Strict,
            Token);
        Assert.True(far.Passed, far.Summary());
        Assert.Equal(1, far.TolerancePairs);

        var near = NifGlbParityOracle.Compare(Encode(placement: Matrix4x4.CreateTranslation(1f, 0f, 0f)),
            Encode(placement: Matrix4x4.CreateTranslation(1.05f, 0f, 0f)), NifGlbParityExpectations.Strict, Token);
        Assert.False(near.Passed);
    }

    /// <summary>
    ///     Values straddling a five-decimal rounding boundary and a 1e-3 cell boundary still pair, where the retired
    ///     quantized identity strings split them; a difference beyond tolerance still fails.
    /// </summary>
    [Fact]
    public void RoundingAndCellBoundaries_DoNotFlake()
    {
        const float nativeX = 0.0029999998f;
        const float sharedX = 0.0030000002f;
        const float nativeY = 0.1234549f;
        const float sharedY = 0.1234551f;

        // The fixture really does straddle both boundaries.
        Assert.NotEqual(Math.Round(nativeY, 5, MidpointRounding.ToEven),
            Math.Round(sharedY, 5, MidpointRounding.ToEven));
        Assert.NotEqual(Math.Floor(nativeX / 1e-3), Math.Floor(sharedX / 1e-3));

        var report = NifGlbParityOracle.Compare(Encode(positions: Plane(nativeX, nativeY)),
            Encode(positions: Plane(sharedX, sharedY)), NifGlbParityExpectations.Strict, Token);
        Assert.True(report.Passed, report.Summary());
        Assert.Equal(1, report.TolerancePairs);

        var moved = NifGlbParityOracle.Compare(Encode(positions: Plane(nativeX, nativeY)),
            Encode(positions: Plane(0.005f, nativeY)), NifGlbParityExpectations.Strict, Token);
        Assert.False(moved.Passed);
    }

    /// <summary>ExtensionsUsed must match even when every compared surface is identical.</summary>
    [Fact]
    public void ExtensionsUsed_MismatchFails()
    {
        var control = NifGlbParityOracle.Compare(Encode(), Encode(), NifGlbParityExpectations.Strict, Token);
        Assert.True(control.Passed, control.Summary());

        var report = NifGlbParityOracle.Compare(Encode(), Encode(withLight: true), NifGlbParityExpectations.Strict,
            Token);
        Assert.Equal(1, report.FailureCount);
        Assert.Contains("ExtensionsUsed", Assert.Single(report.Failures), StringComparison.Ordinal);
    }

    /// <summary>Layer A accepts the adapter's exact document and rejects it once the source no longer derives it.</summary>
    [Fact]
    public void SourceParity_ExactDocumentPasses_AndAChangedSourceFails()
    {
        using var resolver = Resolver();
        var scene = Scene(("surface", false, [Triangle(0f)]));
        var plan = NifGlbParityCorpus.PlanNormalized(scene, resolver, Request(), Token);
        Assert.True(plan.Decision.IsNormalized, $"{plan.Decision.Stage}: {plan.Decision.Reason}");

        var mapping = NifGlbSourceParity.Check(scene, plan.ModelDocument!, resolver, Token);
        Assert.Equal(1, mapping.Count);
        Assert.True(mapping.TryGetPartOrdinal(0, 0, out var ordinal));
        Assert.Equal(0, ordinal);
        Assert.False(mapping.HasAuthoredTangentsOrUnknown(0, 0));
        Assert.True(mapping.HasAuthoredTangentsOrUnknown(0, 1));

        scene.MeshParts[0].Submesh.Positions[0] += 0.5f;
        Assert.ThrowsAny<XunitException>(() => NifGlbSourceParity.Check(scene, plan.ModelDocument!, resolver, Token));
    }

    /// <summary>Selection orders by the SHA-256 of the lower-cased forward-slash path, independent of spelling.</summary>
    [Fact]
    public void CorpusSelection_OrdersBySha256OfTheNormalizedRelativePath()
    {
        // Digests computed independently with sha256sum over the UTF-8 path text.
        Assert.Equal("69443CC85C2E55723E404FE78C5B93E98E82B05B4915D28AB6D5101F0306BE0F",
            NifGlbParityCorpus.OrderingKey(@"Meshes\Clutter\Junk\TinCan01.NIF"));
        string[] candidates =
        [
            "meshes/architecture/goodsprings/nv_prospectorsaloon-neon_lights.nif",
            "clutter/junk/tincan01.nif",
            "meshes/clutter/junk/tincan01.nif"
        ];
        string[] sampled = ["meshes/clutter/junk/tincan01.nif", "clutter/junk/tincan01.nif"];
        string[] everything =
        [
            "meshes/clutter/junk/tincan01.nif", "clutter/junk/tincan01.nif",
            "meshes/architecture/goodsprings/nv_prospectorsaloon-neon_lights.nif"
        ];
        Assert.Equal(sampled, NifGlbParityCorpus.Select(candidates, static path => path, 2, false));
        Assert.Equal(everything, NifGlbParityCorpus.Select(candidates, static path => path, 2, true));
    }

    /// <summary>The checked-in ratchet parses, and names only real strata and feature classes.</summary>
    [Fact]
    public void Ratchet_ParsesAndNamesOnlyKnownStrataAndFeatureClasses()
    {
        var ratchet = NifGlbParityRatchet.Load();
        Assert.Equal(1, ratchet.Version);
        foreach (var (id, entry) in ratchet.Strata)
        {
            Assert.Contains(NifGlbParityCorpus.Strata, stratum => stratum.Id == id);
            Assert.All(entry.RequiredFeatureClasses ?? [],
                featureClass => Assert.Contains(featureClass, NifGlbParityOracle.FeatureClassNames));
        }
    }

    /// <summary>The Bucket B theory has exactly one row per defined stratum.</summary>
    [Fact]
    public void CorpusTheory_HasOneRowPerStratum()
    {
        var method = typeof(NifGlbProductionParityCorpusTests).GetMethod(
            nameof(NifGlbProductionParityCorpusTests.Stratum_NormalizedExportMatchesTheNativeWriter));
        Assert.NotNull(method);
        var rows = method.GetCustomAttributes<InlineDataAttribute>().Select(static row => (string)row.Data[0]!)
            .Order(StringComparer.Ordinal);
        Assert.Equal(NifGlbParityCorpus.Strata.Select(static stratum => stratum.Id).Order(StringComparer.Ordinal),
            rows);
    }

    /// <summary>
    ///     SharedValidation always fails a stratum; with a frozen entry, an unlisted decline reason and a ceiling
    ///     overrun fail too, while an empty ratchet only applies the unconditional rules.
    /// </summary>
    [Fact]
    public void Violations_CountSharedValidationAndEnforceAFrozenAllowlist()
    {
        var stratum = NifGlbParityCorpus.Stratum("S9");
        var clean = CleanReceipt(stratum);
        Assert.Empty(NifGlbParityCorpus.Violations(stratum, clean, null));
        Assert.Equal("empty", clean.Ratchet);

        var faulted = CleanReceipt(stratum);
        faulted.SharedValidation = 1;
        faulted.SharedValidationFiles["x.nif"] = "invalid";
        Assert.Single(NifGlbParityCorpus.Violations(stratum, faulted, null));

        var declined = CleanReceipt(stratum);
        declined.RecordDecline("a.nif", "Adapter: listed");
        declined.RecordDecline("b.nif", "Adapter: listed");
        declined.RecordDecline("c.nif", "Adapter: unlisted");
        var entry = new NifGlbParityRatchetEntry
        {
            DeclineCeilings = new Dictionary<string, int> { ["Adapter: listed"] = 1 }
        };
        var violations = NifGlbParityCorpus.Violations(stratum, declined, entry);
        Assert.Equal(2, violations.Count);
        Assert.Equal("frozen", declined.Ratchet);
        Assert.Empty(NifGlbParityCorpus.Violations(stratum, CleanReceipt(stratum), entry));
    }

    /// <summary>A receipt that satisfies every unconditional and stratum-specific rule of <paramref name="stratum" />.</summary>
    private static NifGlbParityStratumReceipt CleanReceipt(NifGlbParityStratum stratum)
    {
        var receipt = new NifGlbParityStratumReceipt
        {
            StratumId = stratum.Id,
            Candidates = stratum.SampleSize,
            Selected = stratum.SampleSize,
            Inspected = stratum.SampleSize,
            ConvertedFromBigEndian = 1
        };
        receipt.ComparedPaths.AddRange(stratum.RequiredComparedPaths);
        receipt.Compared = receipt.ComparedPaths.Count;
        return receipt;
    }

    /// <summary>A resolver that finds no texture, so every material is prepared from factors alone.</summary>
    private static NifTextureResolver Resolver() => new(static _ => null);

    /// <summary>A normalized request for a synthetic FO3/FNV-family scene.</summary>
    private static NifGlbExportRequest Request() =>
        new("synthetic", NifExportFamily.Fo3Fnv, false, NifGlbWriterPreference.Normalized);

    /// <summary>A unit right triangle in the plane z = <paramref name="z" />, wound counter-clockwise about +Z.</summary>
    private static float[] Triangle(float z) => [0f, 0f, z, 1f, 0f, z, 0f, 1f, z];

    /// <summary>A triangle whose second and third corners share one position, which the native toolkit rejects.</summary>
    private static float[] RepeatedTriangle(float z) => [0f, 0f, z, 1f, 0f, z, 1f, 0f, z];

    /// <summary>A triangle in the plane x = <paramref name="x" /> anchored at y = <paramref name="y" />.</summary>
    private static Vector3[] Plane(float x, float y) => [new(x, y, 0f), new(x, y + 1f, 0f), new(x, y, 1f)];

    /// <summary>Builds a source scene of rigid parts on the root node, each part a list of triangles.</summary>
    private static GlbScene Scene(params (string Name, bool DoubleSided, float[][] Triangles)[] parts)
    {
        var scene = new GlbScene();
        foreach (var (name, doubleSided, triangles) in parts)
        {
            var positions = triangles.SelectMany(static triangle => triangle).ToArray();
            var count = positions.Length / 3;
            float[][] corners = [[0f, 0f], [1f, 0f], [0f, 1f]];
            scene.MeshParts.Add(new GlbMeshPart
            {
                Name = name,
                NodeIndex = GlbScene.RootNodeIndex,
                Submesh = new RenderableSubmesh
                {
                    Positions = positions,
                    Triangles = Enumerable.Range(0, count).Select(static index => (ushort)index).ToArray(),
                    Normals = Enumerable.Range(0, count).SelectMany(static _ => new[] { 0f, 0f, 1f }).ToArray(),
                    UVs = Enumerable.Range(0, count).SelectMany(index => corners[index % 3]).ToArray(),
                    IsDoubleSided = doubleSided
                }
            });
        }

        return scene;
    }

    /// <summary>Encodes a one-triangle shared document with exactly one varied property.</summary>
    private static ModelRoot Encode(Vector3[]? positions = null, Vector4? color = null,
        SceneColorEncoding encoding = SceneColorEncoding.FloatingPoint, Matrix4x4? placement = null,
        string materialName = "material", bool withLight = false)
    {
        var vertexColor = color ?? Vector4.One;
        var vertices = (positions ?? [Vector3.Zero, Vector3.UnitX, Vector3.UnitY])
            .Select(position => new SceneVertex(position, Vector3.UnitZ, vertexColor, Vector2.Zero));
        var document = new ModelDocument("synthetic", "parity oracle",
            [new SceneDefinition("scene", [0])],
            [new SceneNode("placement", placement ?? Matrix4x4.Identity, meshIndex: 0)],
            [new SceneMesh("mesh", [new ScenePrimitive("surface", vertices, [0, 1, 2], 0, encoding)])],
            [new SceneMaterial(materialName, Vector4.One)]);
        var built = SceneGltfBuilder.Build(document, GltfExportIntent.Interchange, Token);
        if (withLight)
        {
            // A light is not compared by any geometry or material rule; it only declares KHR_lights_punctual.
            built.Model.CreatePunctualLight(PunctualLightType.Point);
        }

        return ModelRoot.ParseGLB(GltfExporter.Encode(built, Token));
    }

    /// <summary>One native-style toolkit vertex with a fixed normal and tangent.</summary>
    private static (VertexPositionNormalTangent Geometry, VertexColor1Texture1 Material) ToolkitVertex(
        Vector3 position, Vector4 color) =>
        (new VertexPositionNormalTangent(position, Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f)),
            new VertexColor1Texture1(color, Vector2.Zero));

    /// <summary>Counts every primitive's triangles in a parsed GLB.</summary>
    private static int TriangleCount(ModelRoot model) =>
        model.LogicalMeshes.Sum(static mesh =>
            mesh.Primitives.Sum(static primitive => primitive.GetIndices().Count / 3));
}

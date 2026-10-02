using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Exercises the NIF GLB router on synthetic scenes. Admission is data, so tests admit a family through the
///     predicate overload; the production table stays empty and is pinned as such here.
/// </summary>
/// <remarks>
///     Every byte comparison is against an independently produced artifact: the native writer run on a fresh,
///     identical scene, or the adapter, shared build and shared encoder run directly. A router that returned the
///     wrong writer's bytes fails on content, not on a flag.
/// </remarks>
public sealed class NifGlbExportTests
{
    private const string Label = "fixture";
    private const string Diffuse = "surface.dds";

    private static readonly Func<NifExportFamily, bool, bool> AdmitAll = static (_, _) => true;

    /// <summary>An admitted static textured scene takes the normalized route and its bytes are the shared encoder's.</summary>
    [Theory]
    [InlineData(NifGlbWriterPreference.Auto)]
    [InlineData(NifGlbWriterPreference.Normalized)]
    internal void StaticTexturedScene_TakesTheNormalizedRouteWhenAdmitted(NifGlbWriterPreference preference)
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = TexturedResolver();
        var scene = Scene(Surface(Diffuse));
        var original = new NifCorpusSourceSnapshot(scene);

        var plan = NifGlbExport.Plan(scene, resolver, Request(preference), AdmitAll, token);

        original.AssertUnchanged(scene);
        Assert.Equal(new NifGlbExportDecision(NifGlbExportRoute.Normalized, preference, NifGlbDeclineStage.None,
            null, false), plan.Decision);
        Assert.NotNull(plan.ModelDocument);
        Assert.NotNull(plan.GltfDocument);
        Assert.Equal(GltfExportIntent.Interchange, plan.GltfDocument.Intent);
        Assert.Equal(Label, plan.ModelDocument.Name);
        Assert.Single(plan.ModelDocument.Images);
        Assert.NotNull(Assert.Single(plan.ModelDocument.Materials).Texture);

        var bytes = NifGlbExport.WriteToBytes(plan, scene, resolver, token);

        Assert.Equal(SharedBytes(Scene(Surface(Diffuse)), resolver), bytes);
        Assert.NotEqual(GlbWriter.WriteToBytes(Scene(Surface(Diffuse)), resolver), bytes);
    }

    /// <summary>The production admission table is empty in this round, for both conversion states.</summary>
    [Fact]
    public void ProductionAdmissionTable_AdmitsNoFamily()
    {
        foreach (var family in Enum.GetValues<NifExportFamily>())
        {
            Assert.False(NifGlbNormalizedAdmission.IsAdmitted(family, convertedFromBigEndian: false), family.ToString());
            Assert.False(NifGlbNormalizedAdmission.IsAdmitted(family, convertedFromBigEndian: true), family.ToString());
        }
    }

    /// <summary>Under Auto an unadmitted family falls back to the native writer with the exact family reason.</summary>
    [Theory]
    [InlineData(false, "The Fo3Fnv family is not admitted to the normalized GLB writer.")]
    [InlineData(true, "The Fo3Fnv family, converted from big-endian, is not admitted to the normalized GLB writer.")]
    public void UnadmittedFamily_UsesTheNativeWriterUnderAuto(bool convertedFromBigEndian, string reason)
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = TexturedResolver();
        var scene = Scene(Surface(Diffuse));

        var plan = NifGlbExport.Plan(scene, resolver,
            Request(NifGlbWriterPreference.Auto, NifExportFamily.Fo3Fnv, convertedFromBigEndian), token);

        Assert.Equal(new NifGlbExportDecision(NifGlbExportRoute.Native, NifGlbWriterPreference.Auto,
            NifGlbDeclineStage.FamilyNotAdmitted, reason, false), plan.Decision);
        Assert.Null(plan.ModelDocument);
        Assert.Null(plan.GltfDocument);
        Assert.Equal(GlbWriter.WriteToBytes(Scene(Surface(Diffuse)), resolver),
            NifGlbExport.WriteToBytes(plan, scene, resolver, token));
    }

    /// <summary>Under Normalized an unadmitted family is refused and no bytes are produced.</summary>
    [Fact]
    public void UnadmittedFamily_IsRefusedUnderNormalizedAndWritesNothing()
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = TexturedResolver();
        var scene = Scene(Surface(Diffuse));

        var plan = NifGlbExport.Plan(scene, resolver,
            Request(NifGlbWriterPreference.Normalized, NifExportFamily.Skyrim), token);

        Assert.Equal(new NifGlbExportDecision(NifGlbExportRoute.Normalized, NifGlbWriterPreference.Normalized,
            NifGlbDeclineStage.FamilyNotAdmitted, "The Skyrim family is not admitted to the normalized GLB writer.",
            true), plan.Decision);
        Assert.Null(plan.ModelDocument);
        Assert.Null(plan.GltfDocument);
        Assert.Throws<InvalidOperationException>(() => NifGlbExport.WriteToBytes(plan, scene, resolver, token));
    }

    /// <summary>A Native request never runs admission, the adapter or the shared build.</summary>
    [Fact]
    public void NativePreference_NeverConsultsAdmissionOrTheSharedBuild()
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = TexturedResolver();
        var scene = Scene(Surface(Diffuse));

        var plan = NifGlbExport.Plan(scene, resolver, Request(NifGlbWriterPreference.Native),
            static (_, _) => throw new InvalidOperationException("admission was consulted"),
            static (_, _, _) => throw new InvalidOperationException("the shared build was consulted"),
            token);

        Assert.Equal(new NifGlbExportDecision(NifGlbExportRoute.Native, NifGlbWriterPreference.Native,
            NifGlbDeclineStage.Requested, NifGlbExportRouting.NativeRequestedReason, false), plan.Decision);
        Assert.Equal(GlbWriter.WriteToBytes(Scene(Surface(Diffuse)), resolver),
            NifGlbExport.WriteToBytes(plan, scene, resolver, token));
    }

    /// <summary>
    ///     Admitted but ineligible scenes fall back to the native writer under Auto with the exact reason, and are
    ///     refused under Normalized. "ViewerExtensionSource" is the CE2 varying vertex-Lerp surface, the only source
    ///     state besides water whose preparation emits viewer extras; the adapter declines its Starfield render state
    ///     before preparation, so that is the reason it reports.
    /// </summary>
    [Theory]
    [InlineData("SeparateSpecularMap", NifGlbDeclineStage.Adapter,
        "Maps beyond diffuse retain the native material writer.")]
    [InlineData("WaterOptics", NifGlbDeclineStage.Adapter,
        "Water optical material channels retain the native material writer.")]
    [InlineData("ViewerExtensionSource", NifGlbDeclineStage.Adapter,
        "Starfield material render state retains the native material writer.")]
    [InlineData("SkinnedPart", NifGlbDeclineStage.Scope,
        "A skinned mesh part is outside the normalized static export scope.")]
    internal void IneligibleScene_UsesTheNativeWriterWithTheExactReason(
        string shape,
        NifGlbDeclineStage stage,
        string reason)
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = new NifTextureResolver(_ => null);
        var scene = Ineligible(shape);
        var original = new NifCorpusSourceSnapshot(scene);

        var plan = NifGlbExport.Plan(scene, resolver, Request(NifGlbWriterPreference.Auto), AdmitAll, token);

        original.AssertUnchanged(scene);
        Assert.Equal(new NifGlbExportDecision(NifGlbExportRoute.Native, NifGlbWriterPreference.Auto, stage, reason,
            false), plan.Decision);
        Assert.Null(plan.ModelDocument);
        Assert.Null(plan.GltfDocument);
        Assert.Equal(GlbWriter.WriteToBytes(Ineligible(shape), resolver),
            NifGlbExport.WriteToBytes(plan, scene, resolver, token));

        var refused = NifGlbExport.Plan(Ineligible(shape), resolver, Request(NifGlbWriterPreference.Normalized),
            AdmitAll, token);
        Assert.Equal(new NifGlbExportDecision(NifGlbExportRoute.Normalized, NifGlbWriterPreference.Normalized, stage,
            reason, true), refused.Decision);
        Assert.Throws<InvalidOperationException>(() =>
            NifGlbExport.WriteToBytes(refused, Ineligible(shape), resolver, token));
    }

    /// <summary>A NotSupportedException from the shared build is the SharedBuild stage, after the adapter has run.</summary>
    [Theory]
    [InlineData(NifGlbWriterPreference.Auto, NifGlbExportRoute.Native, false)]
    [InlineData(NifGlbWriterPreference.Normalized, NifGlbExportRoute.Normalized, true)]
    internal void SharedBuildNotSupported_IsTheSharedBuildStage(
        NifGlbWriterPreference preference,
        NifGlbExportRoute route,
        bool refused)
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = TexturedResolver();
        var scene = Scene(Surface(Diffuse));
        ModelDocument? built = null;

        var plan = NifGlbExport.Plan(scene, resolver, Request(preference), AdmitAll,
            (document, intent, _) =>
            {
                built = document;
                Assert.Equal(GltfExportIntent.Interchange, intent);
                throw new NotSupportedException("probe: the shared build declined");
            },
            token);

        // The adapter ran and handed its document to the build before the build declined.
        Assert.NotNull(built);
        Assert.Equal(Label, built.Name);
        Assert.Equal(new NifGlbExportDecision(route, preference, NifGlbDeclineStage.SharedBuild,
            "probe: the shared build declined", refused), plan.Decision);
        Assert.Null(plan.ModelDocument);
        Assert.Null(plan.GltfDocument);
    }

    /// <summary>
    ///     A shared validation fault is its own stage (the corpus gate counts it as a failure). The build seam hands
    ///     the real shared builder a document whose default scene index is out of range, so the fault is raised by
    ///     the shared validation itself, not by this test.
    /// </summary>
    [Theory]
    [InlineData(NifGlbWriterPreference.Auto, NifGlbExportRoute.Native, false)]
    [InlineData(NifGlbWriterPreference.Normalized, NifGlbExportRoute.Normalized, true)]
    internal void SharedValidationFault_IsTheSharedValidationStage(
        NifGlbWriterPreference preference,
        NifGlbExportRoute route,
        bool refused)
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = TexturedResolver();
        var scene = Scene(Surface(Diffuse));

        var plan = NifGlbExport.Plan(scene, resolver, Request(preference), AdmitAll,
            static (document, intent, cancellation) =>
                SceneGltfBuilder.Build(WithOutOfRangeDefaultScene(document), intent, cancellation),
            token);

        Assert.Equal(new NifGlbExportDecision(route, preference, NifGlbDeclineStage.SharedValidation,
            "Invalid default scene index 5 for 1 entries.", refused), plan.Decision);
        Assert.Null(plan.ModelDocument);
        Assert.Null(plan.GltfDocument);
    }

    /// <summary>Only shared validation faults are routed; the same exception type from elsewhere is an error.</summary>
    [Fact]
    public void InvalidDataOutsideTheSharedAssemblies_Propagates()
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = TexturedResolver();

        var fromBuild = Assert.Throws<InvalidDataException>(() => NifGlbExport.Plan(Scene(Surface(Diffuse)),
            resolver, Request(NifGlbWriterPreference.Auto), AdmitAll,
            static (_, _, _) => throw new InvalidDataException("not a shared fault"), token));
        Assert.Equal("not a shared fault", fromBuild.Message);

        using var failing = new NifTextureResolver(_ => throw new InvalidDataException("malformed texture fixture"));
        var fromResolver = Assert.Throws<InvalidDataException>(() => NifGlbExport.Plan(Scene(Surface(Diffuse)),
            failing, Request(NifGlbWriterPreference.Auto), AdmitAll, token));
        Assert.Equal("malformed texture fixture", fromResolver.Message);
    }

    /// <summary>
    ///     The native writer normalizes winding on the source in place, so the adapter must run first. Normals face
    ///     -Z while the triangle winds counter-clockwise about +Z, so both writers flip it.
    /// </summary>
    [Fact]
    public void Planning_RunsTheAdapterBeforeTheNativeWriterTouchesTheSource()
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = new NifTextureResolver(_ => null);
        var scene = Scene(Surface(normalZ: -1f));
        var original = new NifCorpusSourceSnapshot(scene);
        ModelDocument? planned = null;

        var plan = NifGlbExport.Plan(scene, resolver, Request(NifGlbWriterPreference.Auto), AdmitAll,
            (document, _, _) =>
            {
                planned = document;
                throw new NotSupportedException("probe");
            },
            token);

        // Planning left the source as assembled; the adapter flipped its own clone.
        original.AssertUnchanged(scene);
        Assert.Equal(new ushort[] { 0, 1, 2 }, scene.MeshParts[0].Submesh.Triangles);
        Assert.NotNull(planned);
        Assert.Equal(new[] { 0, 2, 1 }, planned.Meshes[0].Primitives[0].Indices.ToArray());

        // The native write that follows is the first thing to modify the source.
        var bytes = NifGlbExport.WriteToBytes(plan, scene, resolver, token);

        Assert.Equal(new ushort[] { 0, 2, 1 }, scene.MeshParts[0].Submesh.Triangles);
        Assert.Equal(GlbWriter.WriteToBytes(Scene(Surface(normalZ: -1f)), resolver), bytes);
    }

    /// <summary>Cancellation before planning, during admission, during the shared build and before writing propagates.</summary>
    [Fact]
    public void Cancellation_IsObservedAndNeverReportedAsADecline()
    {
        using var resolver = TexturedResolver();

        using var before = new CancellationTokenSource();
        before.Cancel();
        Assert.Throws<OperationCanceledException>(() => NifGlbExport.Plan(Scene(Surface(Diffuse)), resolver,
            Request(NifGlbWriterPreference.Auto),
            static (_, _) => throw new InvalidOperationException("admission ran after cancellation"), before.Token));

        using var duringAdmission = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => NifGlbExport.Plan(Scene(Surface(Diffuse)), resolver,
            Request(NifGlbWriterPreference.Auto),
            (_, _) =>
            {
                duringAdmission.Cancel();
                return true;
            },
            duringAdmission.Token));

        using var duringBuild = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => NifGlbExport.Plan(Scene(Surface(Diffuse)), resolver,
            Request(NifGlbWriterPreference.Auto), AdmitAll,
            (document, intent, cancellation) =>
            {
                duringBuild.Cancel();
                return SceneGltfBuilder.Build(document, intent, cancellation);
            },
            duringBuild.Token));

        var plan = NifGlbExport.Plan(Scene(Surface(Diffuse)), resolver, Request(NifGlbWriterPreference.Auto),
            AdmitAll, TestContext.Current.CancellationToken);
        Assert.True(plan.Decision.IsNormalized);
        Assert.Throws<OperationCanceledException>(() =>
            NifGlbExport.WriteToBytes(plan, Scene(Surface(Diffuse)), resolver, before.Token));
    }

    /// <summary>Write publishes the chosen route's bytes, and a refused plan creates neither the file nor its directory.</summary>
    [Fact]
    public void Write_PublishesTheChosenRouteAndRefusalWritesNothing()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "bmt-nif-glb-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var resolver = TexturedResolver();

            var normalizedScene = Scene(Surface(Diffuse));
            var normalizedPlan = NifGlbExport.Plan(normalizedScene, resolver, Request(NifGlbWriterPreference.Auto),
                AdmitAll, token);
            var normalizedPath = Path.Combine(root, "normalized", "out.glb");
            NifGlbExport.Write(normalizedPlan, normalizedScene, resolver, normalizedPath, token);
            Assert.Equal(SharedBytes(Scene(Surface(Diffuse)), resolver), File.ReadAllBytes(normalizedPath));

            // The native route publishes exactly as the native writer does on its own.
            var nativeScene = Scene(Surface(Diffuse));
            var nativePlan = NifGlbExport.Plan(nativeScene, resolver, Request(NifGlbWriterPreference.Native), token);
            var nativePath = Path.Combine(root, "native", "out.glb");
            NifGlbExport.Write(nativePlan, nativeScene, resolver, nativePath, token);
            var referencePath = Path.Combine(root, "reference", "out.glb");
            GlbWriter.Write(Scene(Surface(Diffuse)), resolver, referencePath);
            Assert.Equal(File.ReadAllBytes(referencePath), File.ReadAllBytes(nativePath));

            var refusedScene = Scene(Surface(Diffuse));
            var refusedPlan = NifGlbExport.Plan(refusedScene, resolver, Request(NifGlbWriterPreference.Normalized),
                token);
            var refusedDirectory = Path.Combine(root, "refused");
            Assert.True(refusedPlan.Decision.Refused);
            Assert.Throws<InvalidOperationException>(() => NifGlbExport.Write(refusedPlan, refusedScene, resolver,
                Path.Combine(refusedDirectory, "out.glb"), token));
            Assert.False(Directory.Exists(refusedDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>A plan carries documents exactly when its decision takes the normalized route.</summary>
    [Fact]
    public void Plan_RejectsDocumentsThatDisagreeWithItsRoute()
    {
        using var resolver = TexturedResolver();
        Assert.True(NifNeutralSceneAdapter.TryAdapt(Scene(Surface(Diffuse)), resolver, Label, out var document,
            out var reason, TestContext.Current.CancellationToken), reason);

        var normalized = new NifGlbExportDecision(NifGlbExportRoute.Normalized, NifGlbWriterPreference.Auto,
            NifGlbDeclineStage.None, null, false);
        var native = new NifGlbExportDecision(NifGlbExportRoute.Native, NifGlbWriterPreference.Native,
            NifGlbDeclineStage.Requested, NifGlbExportRouting.NativeRequestedReason, false);

        Assert.Throws<ArgumentException>(() => new NifGlbExportPlan(normalized, null, null));
        Assert.Throws<ArgumentException>(() => new NifGlbExportPlan(normalized, document, null));
        Assert.Throws<ArgumentException>(() => new NifGlbExportPlan(native, document, null));
    }

    /// <summary>Missing arguments and an undefined preference are argument errors.</summary>
    [Fact]
    public void Plan_RejectsInvalidArguments()
    {
        var token = TestContext.Current.CancellationToken;
        using var resolver = TexturedResolver();

        Assert.Throws<ArgumentNullException>(() =>
            NifGlbExport.Plan(null!, resolver, Request(NifGlbWriterPreference.Auto), token));
        Assert.Throws<ArgumentNullException>(() =>
            NifGlbExport.Plan(Scene(Surface()), null!, Request(NifGlbWriterPreference.Auto), token));
        Assert.Throws<ArgumentNullException>(() => NifGlbExport.Plan(Scene(Surface()), resolver, null!, token));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NifGlbExport.Plan(Scene(Surface()), resolver, Request((NifGlbWriterPreference)9), token));
    }

    /// <summary>Builds each ineligible fixture fresh, so a native reference never shares a mutated source.</summary>
    private static GlbScene Ineligible(string shape)
    {
        switch (shape)
        {
            case "SeparateSpecularMap":
            {
                var part = Surface();
                part.SpecularMapTexturePath = "surface_s.dds";
                return Scene(part);
            }
            case "WaterOptics":
                return Scene(Surface(RenderableSubmesh.WaterSurfaceTexturePath));
            case "ViewerExtensionSource":
                return Scene(Surface(
                    color: new StarfieldMaterialColorRenderState(StarfieldMaterialColorRenderMode.VertexLerp,
                        Vector4.Zero),
                    colors: [255, 0, 0, 128, 0, 255, 0, 64, 0, 0, 255, 200]));
            case "SkinnedPart":
            {
                var scene = new GlbScene();
                var bone = scene.AddNode("Bone", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
                    GlbNodeKind.Skeleton, "Bone");
                scene.MeshParts.Add(new GlbMeshPart
                {
                    Name = "surface",
                    NodeIndex = bone,
                    Submesh = Surface(),
                    Skin = new GlbSkinBinding
                    {
                        JointNodeIndices = [bone],
                        InverseBindMatrices = [Matrix4x4.Identity],
                        PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
                    }
                });
                return scene;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    /// <summary>A unit triangle winding counter-clockwise about +Z, with explicit normals and UVs.</summary>
    private static RenderableSubmesh Surface(
        string? diffuse = null,
        float normalZ = 1f,
        StarfieldMaterialColorRenderState color = default,
        byte[]? colors = null) => new()
    {
        ShapeName = "surface",
        Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
        Triangles = [0, 1, 2],
        Normals = [0f, 0f, normalZ, 0f, 0f, normalZ, 0f, 0f, normalZ],
        UVs = [0f, 0f, 1f, 0f, 0f, 1f],
        DiffuseTexturePath = diffuse,
        StarfieldMaterialColor = color,
        VertexColors = colors,
        UseVertexColors = colors is not null
    };

    /// <summary>One rigid part attached to its own node under the synthetic root.</summary>
    private static GlbScene Scene(RenderableSubmesh part)
    {
        var scene = new GlbScene();
        var node = scene.AddNode("surface", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "surface");
        scene.MeshParts.Add(new GlbMeshPart { Name = "surface", NodeIndex = node, Submesh = part });
        return scene;
    }

    private static NifGlbExportRequest Request(
        NifGlbWriterPreference preference,
        NifExportFamily family = NifExportFamily.Fo3Fnv,
        bool convertedFromBigEndian = false) => new(Label, family, convertedFromBigEndian, preference);

    /// <summary>Resolves every texture request to one opaque two-texel image.</summary>
    private static NifTextureResolver TexturedResolver() =>
        new(_ => DecodedTexture.FromBaseLevel([10, 20, 30, 255, 40, 50, 60, 255], 2, 1, false));

    /// <summary>The shared route run directly, without the router, on a fresh scene.</summary>
    private static byte[] SharedBytes(GlbScene scene, NifTextureResolver resolver)
    {
        var token = TestContext.Current.CancellationToken;
        Assert.True(NifNeutralSceneAdapter.TryAdapt(scene, resolver, Label, out var document, out var reason, token),
            reason);
        return GltfExporter.Encode(SceneGltfBuilder.Build(document!, GltfExportIntent.Interchange, token), token);
    }

    /// <summary>Copies a document with a default scene index the shared validation must reject.</summary>
    private static ModelDocument WithOutOfRangeDefaultScene(ModelDocument document) => new(
        document.SourceFormat,
        document.Name,
        document.Scenes,
        document.Nodes,
        document.Meshes,
        document.Materials,
        document.Images,
        document.Samplers,
        defaultSceneIndex: 5,
        skins: document.Skins);
}

using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Classifies every public <see cref="RenderableSubmesh" /> property by what the normalized route does with it,
///     so a new property fails here until someone decides whether it is carried, declined or unread.
/// </summary>
/// <remarks>
///     <para>
///         Each classification is checked, not just listed. A carried or unread claim is checked against the source
///         of the native writer's call graph: the files below are every file on <see cref="GlbWriter" />'s path that
///         receives a submesh. A declined claim is checked by behavior: a probe sets the property on an otherwise
///         admitted surface and the adapter must decline with the pinned reason.
///     </para>
///     <para>
///         The call-graph list is itself a claim. A new helper on the writer path that reads a submesh must be added
///         to it, or an unread claim could go stale without failing.
///     </para>
/// </remarks>
public sealed class NifNeutralFidelityCensusTests
{
    private const string MapsReason = "Maps beyond diffuse retain the native material writer.";
    private const string StarfieldStateReason = "Starfield material render state retains the native material writer.";
    private const string OblivionReason = "Authored Oblivion shader inputs retain the native material writer.";
    private const string BillboardReason =
        "SpeedTree billboard orientation is a per-frame runtime behavior and retains the native writer.";
    private const string WindReason =
        "SpeedTree wind-rig speeds drive an unbaked vertex animation and retain the native writer.";
    private const string UnlitEmissionReason = "BGSM emission on an unlit surface retains the native material writer.";

    /// <summary>Files under Core/Formats/Nif/Rendering that the native writer reaches with a submesh.</summary>
    private static readonly string[] WriterCallGraph =
    [
        "Export/GlbWriter.cs",
        "Export/NifMaterialPreparation.cs",
        "Export/NpcGlbTintColorEncoder.cs",
        "Export/NpcGlbAlphaTexturePacker.cs",
        "Export/NpcGlbMaterialTuning.cs",
        "Export/NpcGlbMaterialChannelDecider.cs",
        "Export/AuthoredSkyGlbPreviewProjection.cs",
        "Export/NpcGlbTangentBuilder.cs",
        "Export/StarfieldGlbVertexLerpProjection.cs",
        "Inspection/NifAlphaClassifier.cs",
        "Inspection/NifVertexColorPolicy.cs",
        "Inspection/MeshWindingDiagnostic.cs"
    ];

    private static readonly Lazy<string> WriterSource = new(() => string.Join('\n', WriterCallGraph.Select(path =>
        SourceContract.ReadSource(["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", .. path.Split('/')]))));

    private static readonly Dictionary<string, NifNeutralFidelity> Census = new(StringComparer.Ordinal)
    {
        // Geometry the adapter carries directly (tangent divergence on non-normal-mapped surfaces is measured by
        // the corpus gate, not assumed here).
        [nameof(RenderableSubmesh.Positions)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.Triangles)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.Normals)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.UVs)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.VertexColors)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.Tangents)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.Bitangents)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.VertexCount)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.TriangleCount)] = NifNeutralFidelity.Carried,

        // Material and vertex-color state both writers consume through the same shared helpers.
        [nameof(RenderableSubmesh.ShapeName)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.DiffuseTexturePath)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.ShaderMetadata)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.ClampTextureU)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.ClampTextureV)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.NormalMapTexturePath)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.UseVertexColors)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.UseVertexAlphaForOpacity)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.IsDoubleSided)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.HasAlphaBlend)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.HasAlphaTest)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.AlphaTestThreshold)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.AlphaTestFunction)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.SrcBlendMode)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.DstBlendMode)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.MaterialAlpha)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.MaterialGlossiness)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.IsEyeEnvmap)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.EnvMapScale)] = NifNeutralFidelity.Carried,
        [nameof(RenderableSubmesh.TintColor)] = NifNeutralFidelity.Carried,
        // Sky material and vertex color are shared; the native mesh-name suffix is carried separately (C3).
        [nameof(RenderableSubmesh.SkyType)] = NifNeutralFidelity.Carried,

        // Carried as lit emission; the unlit and malformed states are declined.
        [nameof(RenderableSubmesh.IsEmissive)] = NifNeutralFidelity.CarriedExceptDeclinedStates,
        [nameof(RenderableSubmesh.BgsmGlowMapTexturePath)] = NifNeutralFidelity.CarriedExceptDeclinedStates,
        [nameof(RenderableSubmesh.BgsmEmissionColor)] = NifNeutralFidelity.CarriedExceptDeclinedStates,

        // Every non-default value is an explicit adapter decline.
        [nameof(RenderableSubmesh.StarfieldMaterialColor)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.StarfieldMaterialAlpha)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.IsDecal)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.HasAuthoredOblivionBodySkinInputs)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.HasAuthoredOblivionOrdinaryInputs)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.SpecularMapTexturePath)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.GradientMapTexturePath)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.EnvironmentMapTexturePath)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.ClassicEnvironmentMapTexturePath)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.ClassicEnvironmentMaskTexturePath)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.ClassicParallaxHeightMapTexturePath)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.UsesExternalEmittance)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.EffectFalloff)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.EffectTint)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.IsBillboard)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.IsLeafBillboard)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.IsSpeedTreeBranch)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.SpeedTreeWindSpeeds)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.SpeedTreeLod)] = NifNeutralFidelity.Declined,
        [nameof(RenderableSubmesh.IsFarLodFallback)] = NifNeutralFidelity.Declined,

        // The native writer never reads these, so neither route exports them.
        [nameof(RenderableSubmesh.LegacyMaterialName)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.AuthoredOblivionBodySkinDiffusePath)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.AuthoredOblivionBodySkinAmbientColor)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.AuthoredOblivionOrdinaryDiffusePath)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.LocalBounds)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.GradientMapV)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.EnvironmentMapScale)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.EnvironmentMapSmoothness)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.ClassicEnvironmentMapScale)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.ClassicEnvironmentMapUsesWindowReflection)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.ClassicEnvironmentMapIsSphereMap)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.ExternalEmittanceInfluence)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.SoftParticleFalloffDepth)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.IsTreeAnimation)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.MaterialAlphaController)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.SpecularColor)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.MaterialDiffuse)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.RenderOrder)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.UsesClassicHairMaterial)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.HasAuthoredOblivionHairLayerInputs)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.OblivionHairLayerDiffusePath)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.OblivionHairLayerTexturePath)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.IsFaceGen)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.SubsurfaceColor)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.AnimatedEmissiveColor)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.EmissiveColor)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.Lighting30EmissionColor)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.IsLighting30)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.Lighting30EmissionMultiplier)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.Lighting30GlowMapTexturePath)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.UvScrollVelocity)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.BindPosePositions)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.SourceNifPath)] = NifNeutralFidelity.NotReadByGlbWriter,
        // Skinned neutral placements record it in node extras; the native writer ignores it.
        [nameof(RenderableSubmesh.SourceBlockIndex)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.BillboardMode)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.IsParticleCloud)] = NifNeutralFidelity.NotReadByGlbWriter,
        [nameof(RenderableSubmesh.ParticleRuntime)] = NifNeutralFidelity.NotReadByGlbWriter
    };

    /// <summary>One probe per declined or partly declined property, with the reason the adapter must report.</summary>
    private static readonly Dictionary<string, (Func<RenderableSubmesh> Probe, string Reason)> DeclineProbes =
        new(StringComparer.Ordinal)
        {
            [nameof(RenderableSubmesh.StarfieldMaterialColor)] = (() => Surface(color: new StarfieldMaterialColorRenderState(
                StarfieldMaterialColorRenderMode.ConstantLerp, new Vector4(0.5f, 0.25f, 0.125f, 0.5f))), StarfieldStateReason),
            [nameof(RenderableSubmesh.StarfieldMaterialAlpha)] = (() => Surface(alpha: new StarfieldMaterialAlphaRenderState(
                StarfieldMaterialAlphaRenderMode.Layer0OpacityCutout, 0.5f)), StarfieldStateReason),
            [nameof(RenderableSubmesh.IsDecal)] = (() => Surface(mutate: s => s.IsDecal = true),
                "Decal behavior retains the native material writer."),
            [nameof(RenderableSubmesh.HasAuthoredOblivionBodySkinInputs)] =
                (() => Surface(mutate: s => s.HasAuthoredOblivionBodySkinInputs = true), OblivionReason),
            [nameof(RenderableSubmesh.HasAuthoredOblivionOrdinaryInputs)] =
                (() => Surface(mutate: s => s.HasAuthoredOblivionOrdinaryInputs = true), OblivionReason),
            [nameof(RenderableSubmesh.SpecularMapTexturePath)] =
                (() => Surface(mutate: s => s.SpecularMapTexturePath = "probe_s.dds"), MapsReason),
            [nameof(RenderableSubmesh.GradientMapTexturePath)] =
                (() => Surface(mutate: s => s.GradientMapTexturePath = "probe_grad.dds"), MapsReason),
            [nameof(RenderableSubmesh.EnvironmentMapTexturePath)] =
                (() => Surface(mutate: s => s.EnvironmentMapTexturePath = "probe_e.dds"), MapsReason),
            [nameof(RenderableSubmesh.ClassicEnvironmentMapTexturePath)] =
                (() => Surface(mutate: s => s.ClassicEnvironmentMapTexturePath = "probe_cube.dds"), MapsReason),
            [nameof(RenderableSubmesh.ClassicEnvironmentMaskTexturePath)] =
                (() => Surface(mutate: s => s.ClassicEnvironmentMaskTexturePath = "probe_m.dds"), MapsReason),
            [nameof(RenderableSubmesh.ClassicParallaxHeightMapTexturePath)] =
                (() => Surface(mutate: s => s.ClassicParallaxHeightMapTexturePath = "probe_p.dds"), MapsReason),
            [nameof(RenderableSubmesh.UsesExternalEmittance)] = (() => Surface(mutate: s => s.UsesExternalEmittance = true),
                "Emissive and external-emittance state retains the native material writer."),
            [nameof(RenderableSubmesh.EffectFalloff)] = (() => Surface(mutate: s => s.EffectFalloff = (0f, 1f, 1f, 0f)),
                "Falloff material behavior retains the native material writer."),
            [nameof(RenderableSubmesh.EffectTint)] = (() => Surface(mutate: s => s.EffectTint = (0.5f, 0.5f, 0.5f)),
                "Effect tint retains the native material writer."),
            [nameof(RenderableSubmesh.IsBillboard)] = (() => Surface(mutate: s => s.IsBillboard = true), BillboardReason),
            [nameof(RenderableSubmesh.IsLeafBillboard)] =
                (() => Surface(mutate: s => s.IsLeafBillboard = true), BillboardReason),
            [nameof(RenderableSubmesh.IsSpeedTreeBranch)] = (() => Surface(mutate: WindRig), WindReason),
            [nameof(RenderableSubmesh.SpeedTreeWindSpeeds)] = (() => Surface(mutate: WindRig), WindReason),
            [nameof(RenderableSubmesh.SpeedTreeLod)] = (() => Surface(mutate: s => s.SpeedTreeLod =
                    new SpeedTreeLodMetadata(0, 2, 0f, 100f, SpeedTreeLodComponent.Branch)),
                "SpeedTree level-of-detail selection is a draw-time choice and retains the native writer."),
            [nameof(RenderableSubmesh.IsFarLodFallback)] = (() => Surface(farLodFallback: true),
                "A far-LOD fallback shape retains the native writer."),
            [nameof(RenderableSubmesh.IsEmissive)] = (() => Surface(mutate: s =>
            {
                s.IsEmissive = true;
                s.BgsmEmissionColor = new Vector3(2f, 1f, 4f);
            }), UnlitEmissionReason),
            [nameof(RenderableSubmesh.BgsmGlowMapTexturePath)] = (() => Surface(mutate: s =>
            {
                s.IsEmissive = true;
                s.BgsmGlowMapTexturePath = "probe_glow.dds";
            }), UnlitEmissionReason),
            [nameof(RenderableSubmesh.BgsmEmissionColor)] =
                (() => Surface(mutate: s => s.BgsmEmissionColor = new Vector3(float.NaN, 0.5f, 1f)),
                    "Malformed BGSM emission retains the native material writer.")
        };

    /// <summary>Properties whose classification makes a claim about the native writer's reads.</summary>
    public static TheoryData<string> ReadClaims() => Names(fidelity => fidelity != NifNeutralFidelity.Declined);

    /// <summary>Properties whose classification includes an adapter decline.</summary>
    public static TheoryData<string> DeclineClaims() => Names(fidelity =>
        fidelity is NifNeutralFidelity.Declined or NifNeutralFidelity.CarriedExceptDeclinedStates);

    /// <summary>Every public property has exactly one census row, and every row names a real property.</summary>
    [Fact]
    public void EveryPublicPropertyIsClassified()
    {
        var properties = typeof(RenderableSubmesh)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = properties.Where(name => !Census.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        var stale = Census.Keys.Where(name => !properties.Contains(name)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(unclassified.Length == 0,
            "Classify each new RenderableSubmesh property as carried, declined or not read: " +
            string.Join(", ", unclassified));
        Assert.True(stale.Length == 0, "Remove census rows for properties that no longer exist: " +
                                       string.Join(", ", stale));
    }

    /// <summary>A carried property is read on the native writer's path; an unread one never appears there.</summary>
    [Theory]
    [MemberData(nameof(ReadClaims))]
    public void ReadClaim_MatchesTheNativeWriterCallGraph(string property)
    {
        var read = Regex.IsMatch(WriterSource.Value, @"\." + property + @"\b", RegexOptions.CultureInvariant);

        if (Census[property] == NifNeutralFidelity.NotReadByGlbWriter)
        {
            Assert.False(read, $"{property} is classified as unread but the native writer's call graph reads it.");
        }
        else
        {
            Assert.True(read, $"{property} is classified as carried but the native writer's call graph never reads it.");
        }
    }

    /// <summary>Each declined state is an explicit adapter decline with its exact reason, never a silent drop.</summary>
    [Theory]
    [MemberData(nameof(DeclineClaims))]
    public void DeclinedState_IsAnExplicitAdapterDecline(string property)
    {
        Assert.True(DeclineProbes.TryGetValue(property, out var probe), $"{property} needs a decline probe.");
        using var resolver = new NifTextureResolver(_ => null);

        Assert.False(NifNeutralSceneAdapter.TryAdapt(Scene(probe.Probe()), resolver, "census", out var document,
            out var reason, TestContext.Current.CancellationToken));
        Assert.Null(document);
        Assert.Equal(probe.Reason, reason);
    }

    /// <summary>The probe base itself adapts, so every decline above is caused by its probed property.</summary>
    [Fact]
    public void TheUnprobedSurfaceAdapts()
    {
        using var resolver = new NifTextureResolver(_ => null);

        Assert.True(NifNeutralSceneAdapter.TryAdapt(Scene(Surface()), resolver, "census", out var document,
            out var reason, TestContext.Current.CancellationToken), reason);
        Assert.NotNull(document);
    }

    /// <summary>A decline probe exists only for a property classified as declined or partly declined.</summary>
    [Fact]
    public void EveryProbeBelongsToADeclinedClassification()
    {
        foreach (var property in DeclineProbes.Keys)
        {
            Assert.True(Census.TryGetValue(property, out var fidelity), $"{property} has a probe but no census row.");
            Assert.True(fidelity is NifNeutralFidelity.Declined or NifNeutralFidelity.CarriedExceptDeclinedStates,
                $"{property} has a decline probe but is classified {fidelity}.");
        }
    }

    private static TheoryData<string> Names(Func<NifNeutralFidelity, bool> include)
    {
        var data = new TheoryData<string>();
        foreach (var (name, fidelity) in Census.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (include(fidelity))
            {
                data.Add(name);
            }
        }

        return data;
    }

    private static void WindRig(RenderableSubmesh submesh)
    {
        submesh.IsSpeedTreeBranch = true;
        submesh.SpeedTreeWindSpeeds = new Vector2(2f, 2f);
    }

    /// <summary>An admitted unit triangle; init-only states are parameters, settable ones go through the mutator.</summary>
    private static RenderableSubmesh Surface(
        StarfieldMaterialColorRenderState color = default,
        StarfieldMaterialAlphaRenderState alpha = default,
        bool farLodFallback = false,
        Action<RenderableSubmesh>? mutate = null)
    {
        var submesh = new RenderableSubmesh
        {
            ShapeName = "surface",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            StarfieldMaterialColor = color,
            StarfieldMaterialAlpha = alpha,
            IsFarLodFallback = farLodFallback
        };
        mutate?.Invoke(submesh);
        return submesh;
    }

    private static GlbScene Scene(RenderableSubmesh part)
    {
        var scene = new GlbScene();
        var node = scene.AddNode("surface", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "surface");
        scene.MeshParts.Add(new GlbMeshPart { Name = "surface", NodeIndex = node, Submesh = part });
        return scene;
    }
}

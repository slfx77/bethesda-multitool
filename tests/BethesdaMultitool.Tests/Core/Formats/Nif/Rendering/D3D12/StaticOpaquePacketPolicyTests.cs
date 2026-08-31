using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class StaticOpaquePacketPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Supported_games_admit_an_immutable_modern_ordinary_batch(int game)
    {
        Assert.Equal(
            StaticOpaquePacketFallbackReason.None,
            StaticOpaquePacketPolicy.Resolve(EligibleFacts() with { Game = (StaticOpaquePacketGame)game }));
    }

    [Theory]
    [InlineData(DeniedFact.Disabled, 1)]
    [InlineData(DeniedFact.UnknownGame, 2)]
    [InlineData(DeniedFact.NonOrdinaryLane, 3)]
    [InlineData(DeniedFact.NonModernShader, 4)]
    [InlineData(DeniedFact.Grass, 5)]
    [InlineData(DeniedFact.TallGrass, 6)]
    [InlineData(DeniedFact.SpeedTree, 7)]
    [InlineData(DeniedFact.Leaf, 8)]
    [InlineData(DeniedFact.PhysicsLiteSway, 9)]
    [InlineData(DeniedFact.RigidNodeAnimation, 10)]
    [InlineData(DeniedFact.Skin, 11)]
    [InlineData(DeniedFact.LiveParticles, 12)]
    [InlineData(DeniedFact.UvScroll, 13)]
    [InlineData(DeniedFact.Diagnostics, 14)]
    [InlineData(DeniedFact.Heatmap, 15)]
    [InlineData(DeniedFact.TexturesPending, 16)]
    [InlineData(DeniedFact.EnvironmentPending, 17)]
    [InlineData(DeniedFact.MirrorReplayUnsupported, 18)]
    [InlineData(DeniedFact.RefilterInputsMissing, 19)]
    public void Changing_any_single_safety_fact_fails_closed(
        DeniedFact deniedFact,
        int expected)
    {
        Assert.Equal((StaticOpaquePacketFallbackReason)expected,
            StaticOpaquePacketPolicy.Resolve(DenyOneFact(deniedFact)));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 19)]
    public void Exact_refilter_is_supported_only_with_its_complete_key(
        bool refilterActive,
        bool inputsCaptured,
        int expected)
    {
        var facts = EligibleFacts() with
        {
            ExactRefilterActive = refilterActive,
            ExactRefilterInputsCaptured = inputsCaptured,
        };

        Assert.Equal((StaticOpaquePacketFallbackReason)expected, StaticOpaquePacketPolicy.Resolve(facts));
    }

    [Theory]
    [InlineData(KeyDifference.PublicationIdentity)]
    [InlineData(KeyDifference.PublicationGeneration)]
    [InlineData(KeyDifference.EvictionGeneration)]
    [InlineData(KeyDifference.RenderOrigin)]
    [InlineData(KeyDifference.RefilterMode)]
    [InlineData(KeyDifference.Frustum)]
    [InlineData(KeyDifference.CylinderX)]
    [InlineData(KeyDifference.CylinderY)]
    [InlineData(KeyDifference.CylinderRadius)]
    [InlineData(KeyDifference.SmallPropCutoff)]
    [InlineData(KeyDifference.DistanceLodMode)]
    [InlineData(KeyDifference.ShadowCapture)]
    [InlineData(KeyDifference.CascadePrefixMode)]
    public void Every_packet_dependency_participates_in_reuse_identity(KeyDifference difference)
    {
        var original = CreateKey();
        var changed = ChangeKey(difference);

        Assert.False(StaticOpaquePacketPolicy.CanReuse(original, changed));
    }

    [Fact]
    public void Refilter_float_identity_is_bit_exact()
    {
        var positiveZero = CreateKey(cylinderX: +0f);
        var negativeZero = CreateKey(cylinderX: -0f);
        var firstNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC0_0001));
        var secondNaN = BitConverter.Int32BitsToSingle(unchecked((int)0x7FC0_0002));

        Assert.False(StaticOpaquePacketPolicy.CanReuse(positiveZero, negativeZero));
        Assert.False(StaticOpaquePacketPolicy.CanReuse(
            CreateKey(cylinderX: firstNaN),
            CreateKey(cylinderX: secondNaN)));
        Assert.True(StaticOpaquePacketPolicy.CanReuse(
            CreateKey(cylinderX: firstNaN),
            CreateKey(cylinderX: firstNaN)));
    }

    [Fact]
    public void Inactive_refilter_canonicalizes_all_unused_cull_inputs()
    {
        var first = CreateKey(
            exactRefilterActive: false,
            frustum: CreateFrustum(0f),
            cylinderX: 1f,
            cylinderY: 2f,
            cylinderRadius: 3f,
            smallPropCutoff: 4f);
        var second = CreateKey(
            exactRefilterActive: false,
            frustum: CreateFrustum(100f),
            cylinderX: 11f,
            cylinderY: 12f,
            cylinderRadius: 13f,
            smallPropCutoff: 14f);

        Assert.True(StaticOpaquePacketPolicy.CanReuse(first, second));
    }

    private static StaticOpaquePacketFacts EligibleFacts() => new(
        Requested: true,
        Game: StaticOpaquePacketGame.Fallout76,
        IsOrdinaryLane: true,
        UsesModernStandardShader: true,
        IsGrass: false,
        IsTallGrass: false,
        HasSpeedTree: false,
        IsLeaf: false,
        HasPhysicsLiteSway: false,
        HasRigidNodeAnimation: false,
        HasSkin: false,
        HasLiveParticles: false,
        HasUvScroll: false,
        DiagnosticsEnabled: false,
        HeatmapEnabled: false,
        TexturesTerminal: true,
        EnvironmentMapTerminal: true,
        MirrorReplaySupported: true,
        ExactRefilterActive: true,
        ExactRefilterInputsCaptured: true);

    private static StaticOpaquePacketFacts DenyOneFact(DeniedFact fact) => fact switch
    {
        DeniedFact.Disabled => EligibleFacts() with { Requested = false },
        DeniedFact.UnknownGame => EligibleFacts() with { Game = StaticOpaquePacketGame.Unknown },
        DeniedFact.NonOrdinaryLane => EligibleFacts() with { IsOrdinaryLane = false },
        DeniedFact.NonModernShader => EligibleFacts() with { UsesModernStandardShader = false },
        DeniedFact.Grass => EligibleFacts() with { IsGrass = true },
        DeniedFact.TallGrass => EligibleFacts() with { IsTallGrass = true },
        DeniedFact.SpeedTree => EligibleFacts() with { HasSpeedTree = true },
        DeniedFact.Leaf => EligibleFacts() with { IsLeaf = true },
        DeniedFact.PhysicsLiteSway => EligibleFacts() with { HasPhysicsLiteSway = true },
        DeniedFact.RigidNodeAnimation => EligibleFacts() with { HasRigidNodeAnimation = true },
        DeniedFact.Skin => EligibleFacts() with { HasSkin = true },
        DeniedFact.LiveParticles => EligibleFacts() with { HasLiveParticles = true },
        DeniedFact.UvScroll => EligibleFacts() with { HasUvScroll = true },
        DeniedFact.Diagnostics => EligibleFacts() with { DiagnosticsEnabled = true },
        DeniedFact.Heatmap => EligibleFacts() with { HeatmapEnabled = true },
        DeniedFact.TexturesPending => EligibleFacts() with { TexturesTerminal = false },
        DeniedFact.EnvironmentPending => EligibleFacts() with { EnvironmentMapTerminal = false },
        DeniedFact.MirrorReplayUnsupported => EligibleFacts() with { MirrorReplaySupported = false },
        DeniedFact.RefilterInputsMissing => EligibleFacts() with { ExactRefilterInputsCaptured = false },
        _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, null),
    };

    private static StaticOpaquePacketReuseKey CreateKey(
        ulong publicationIdentity = 101,
        int publicationGeneration = 3,
        int evictionGeneration = 7,
        Vector3? renderOrigin = null,
        bool exactRefilterActive = true,
        Frustum? frustum = null,
        float cylinderX = 10f,
        float cylinderY = 20f,
        float cylinderRadius = 30f,
        float smallPropCutoff = 40f,
        bool distanceLodEnabled = true,
        bool shadowCaptureArmed = true,
        bool useCascadePrefixes = true)
    {
        var resolvedFrustum = frustum ?? CreateFrustum(0f);
        return StaticOpaquePacketReuseKey.Create(
            publicationIdentity,
            publicationGeneration,
            evictionGeneration,
            renderOrigin ?? new Vector3(1f, 2f, 3f),
            exactRefilterActive,
            resolvedFrustum,
            cylinderX,
            cylinderY,
            cylinderRadius,
            smallPropCutoff,
            distanceLodEnabled,
            shadowCaptureArmed,
            useCascadePrefixes);
    }

    private static StaticOpaquePacketReuseKey ChangeKey(KeyDifference difference) => difference switch
    {
        KeyDifference.PublicationIdentity => CreateKey(publicationIdentity: 102),
        KeyDifference.PublicationGeneration => CreateKey(publicationGeneration: 4),
        KeyDifference.EvictionGeneration => CreateKey(evictionGeneration: 8),
        KeyDifference.RenderOrigin => CreateKey(renderOrigin: new Vector3(1f, 2f, 4f)),
        KeyDifference.RefilterMode => CreateKey(exactRefilterActive: false),
        KeyDifference.Frustum => CreateKey(frustum: CreateFrustum(1f)),
        KeyDifference.CylinderX => CreateKey(cylinderX: 11f),
        KeyDifference.CylinderY => CreateKey(cylinderY: 21f),
        KeyDifference.CylinderRadius => CreateKey(cylinderRadius: 31f),
        KeyDifference.SmallPropCutoff => CreateKey(smallPropCutoff: 41f),
        KeyDifference.DistanceLodMode => CreateKey(distanceLodEnabled: false),
        KeyDifference.ShadowCapture => CreateKey(shadowCaptureArmed: false),
        KeyDifference.CascadePrefixMode => CreateKey(useCascadePrefixes: false),
        _ => throw new ArgumentOutOfRangeException(nameof(difference), difference, null),
    };

    private static Frustum CreateFrustum(float offset) => new(
        new Plane(1f + offset, 2f, 3f, 4f),
        new Plane(5f, 6f, 7f, 8f),
        new Plane(9f, 10f, 11f, 12f),
        new Plane(13f, 14f, 15f, 16f),
        new Plane(17f, 18f, 19f, 20f),
        new Plane(21f, 22f, 23f, 24f));

    public enum DeniedFact
    {
        Disabled,
        UnknownGame,
        NonOrdinaryLane,
        NonModernShader,
        Grass,
        TallGrass,
        SpeedTree,
        Leaf,
        PhysicsLiteSway,
        RigidNodeAnimation,
        Skin,
        LiveParticles,
        UvScroll,
        Diagnostics,
        Heatmap,
        TexturesPending,
        EnvironmentPending,
        MirrorReplayUnsupported,
        RefilterInputsMissing,
    }

    public enum KeyDifference
    {
        PublicationIdentity,
        PublicationGeneration,
        EvictionGeneration,
        RenderOrigin,
        RefilterMode,
        Frustum,
        CylinderX,
        CylinderY,
        CylinderRadius,
        SmallPropCutoff,
        DistanceLodMode,
        ShadowCapture,
        CascadePrefixMode,
    }
}

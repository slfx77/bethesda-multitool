using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

internal enum StaticOpaquePacketGame
{
    Unknown,
    Fallout76,
    Starfield,
}

internal enum StaticOpaquePacketFallbackReason
{
    None,
    Disabled,
    UnsupportedGame,
    NonOrdinaryLane,
    NonModernStandardShader,
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
    TexturesNotTerminal,
    EnvironmentMapNotTerminal,
    MirrorReplayUnsupported,
    RefilterInputsNotCaptured,
    SignatureUnavailable,
    NoEligibleBatches,
    KeyMismatch,
    ShadowTailAllocationFailed,
    BuildFailed,
}

/// <summary>
///     Immutable batch and frame facts used to admit the first persistent opaque packet lane.
///     Every renderer feature that can change geometry, per-instance constants, ordering, or
///     resource bindings is explicit so newly introduced call sites start from a fail-closed state.
/// </summary>
internal readonly record struct StaticOpaquePacketFacts(
    bool Requested,
    StaticOpaquePacketGame Game,
    bool IsOrdinaryLane,
    bool UsesModernStandardShader,
    bool IsGrass,
    bool IsTallGrass,
    bool HasSpeedTree,
    bool IsLeaf,
    bool HasPhysicsLiteSway,
    bool HasRigidNodeAnimation,
    bool HasSkin,
    bool HasLiveParticles,
    bool HasUvScroll,
    bool DiagnosticsEnabled,
    bool HeatmapEnabled,
    bool TexturesTerminal,
    bool EnvironmentMapTerminal,
    bool MirrorReplaySupported,
    bool ExactRefilterActive,
    bool ExactRefilterInputsCaptured);

/// <summary>
///     Pure, fail-closed eligibility policy for persistent ordinary opaque packets.
/// </summary>
internal static class StaticOpaquePacketPolicy
{
    internal static StaticOpaquePacketFallbackReason Resolve(in StaticOpaquePacketFacts facts)
    {
        if (!facts.Requested)
        {
            return StaticOpaquePacketFallbackReason.Disabled;
        }

        if (facts.Game is not (StaticOpaquePacketGame.Fallout76 or StaticOpaquePacketGame.Starfield))
        {
            return StaticOpaquePacketFallbackReason.UnsupportedGame;
        }

        if (!facts.IsOrdinaryLane)
        {
            return StaticOpaquePacketFallbackReason.NonOrdinaryLane;
        }

        if (!facts.UsesModernStandardShader)
        {
            return StaticOpaquePacketFallbackReason.NonModernStandardShader;
        }

        if (facts.IsGrass)
        {
            return StaticOpaquePacketFallbackReason.Grass;
        }

        if (facts.IsTallGrass)
        {
            return StaticOpaquePacketFallbackReason.TallGrass;
        }

        if (facts.HasSpeedTree)
        {
            return StaticOpaquePacketFallbackReason.SpeedTree;
        }

        if (facts.IsLeaf)
        {
            return StaticOpaquePacketFallbackReason.Leaf;
        }

        if (facts.HasPhysicsLiteSway)
        {
            return StaticOpaquePacketFallbackReason.PhysicsLiteSway;
        }

        if (facts.HasRigidNodeAnimation)
        {
            return StaticOpaquePacketFallbackReason.RigidNodeAnimation;
        }

        if (facts.HasSkin)
        {
            return StaticOpaquePacketFallbackReason.Skin;
        }

        if (facts.HasLiveParticles)
        {
            return StaticOpaquePacketFallbackReason.LiveParticles;
        }

        if (facts.HasUvScroll)
        {
            return StaticOpaquePacketFallbackReason.UvScroll;
        }

        if (facts.DiagnosticsEnabled)
        {
            return StaticOpaquePacketFallbackReason.Diagnostics;
        }

        if (facts.HeatmapEnabled)
        {
            return StaticOpaquePacketFallbackReason.Heatmap;
        }

        if (!facts.TexturesTerminal)
        {
            return StaticOpaquePacketFallbackReason.TexturesNotTerminal;
        }

        if (!facts.EnvironmentMapTerminal)
        {
            return StaticOpaquePacketFallbackReason.EnvironmentMapNotTerminal;
        }

        if (!facts.MirrorReplaySupported)
        {
            return StaticOpaquePacketFallbackReason.MirrorReplayUnsupported;
        }

        return facts.ExactRefilterActive && !facts.ExactRefilterInputsCaptured
            ? StaticOpaquePacketFallbackReason.RefilterInputsNotCaptured
            : StaticOpaquePacketFallbackReason.None;
    }

    internal static bool CanReuse(
        in StaticOpaquePacketReuseKey packetKey,
        in StaticOpaquePacketReuseKey frameKey) => packetKey == frameKey;
}

/// <summary>
///     Exact identity of every frame input that can change an otherwise immutable packet's matrices,
///     main draw count, or shadow tail. Floating-point values are stored by bit pattern rather than
///     by <see cref="float.Equals(float)" />, which treats signed zero as equal and canonicalizes NaN
///     equality more broadly than a persistent GPU packet may safely assume.
/// </summary>
internal readonly record struct StaticOpaquePacketReuseKey(
    ulong PublicationIdentity,
    int PublicationGeneration,
    int EvictionGeneration,
    StaticOpaquePacketVector3Bits RenderOrigin,
    bool ExactRefilterActive,
    StaticOpaquePacketFrustumBits Frustum,
    int CylinderXBits,
    int CylinderYBits,
    int CylinderRadiusBits,
    int SmallPropCutoffBits,
    bool DistanceLodEnabled,
    bool ShadowCaptureArmed,
    bool UseCascadePrefixes)
{
    internal static StaticOpaquePacketReuseKey Create(
        ulong publicationIdentity,
        int publicationGeneration,
        int evictionGeneration,
        Vector3 renderOrigin,
        bool exactRefilterActive,
        in Frustum frustum,
        float cylinderX,
        float cylinderY,
        float cylinderRadius,
        float smallPropCutoff,
        bool distanceLodEnabled,
        bool shadowCaptureArmed,
        bool useCascadePrefixes)
    {
        // Cull inputs do not participate when no per-frame exact refilter occurs. Canonicalizing
        // them avoids needless packet misses while preserving exact identity whenever they matter.
        var frustumBits = exactRefilterActive
            ? StaticOpaquePacketFrustumBits.From(frustum)
            : default;

        return new StaticOpaquePacketReuseKey(
            publicationIdentity,
            publicationGeneration,
            evictionGeneration,
            StaticOpaquePacketVector3Bits.From(renderOrigin),
            exactRefilterActive,
            frustumBits,
            exactRefilterActive ? BitConverter.SingleToInt32Bits(cylinderX) : 0,
            exactRefilterActive ? BitConverter.SingleToInt32Bits(cylinderY) : 0,
            exactRefilterActive ? BitConverter.SingleToInt32Bits(cylinderRadius) : 0,
            exactRefilterActive ? BitConverter.SingleToInt32Bits(smallPropCutoff) : 0,
            exactRefilterActive && distanceLodEnabled,
            shadowCaptureArmed,
            useCascadePrefixes);
    }
}

internal readonly record struct StaticOpaquePacketVector3Bits(int X, int Y, int Z)
{
    internal static StaticOpaquePacketVector3Bits From(Vector3 value) => new(
        BitConverter.SingleToInt32Bits(value.X),
        BitConverter.SingleToInt32Bits(value.Y),
        BitConverter.SingleToInt32Bits(value.Z));
}

internal readonly record struct StaticOpaquePacketPlaneBits(int X, int Y, int Z, int D)
{
    internal static StaticOpaquePacketPlaneBits From(Plane value) => new(
        BitConverter.SingleToInt32Bits(value.Normal.X),
        BitConverter.SingleToInt32Bits(value.Normal.Y),
        BitConverter.SingleToInt32Bits(value.Normal.Z),
        BitConverter.SingleToInt32Bits(value.D));
}

internal readonly record struct StaticOpaquePacketFrustumBits(
    StaticOpaquePacketPlaneBits Left,
    StaticOpaquePacketPlaneBits Right,
    StaticOpaquePacketPlaneBits Bottom,
    StaticOpaquePacketPlaneBits Top,
    StaticOpaquePacketPlaneBits Near,
    StaticOpaquePacketPlaneBits Far)
{
    internal static StaticOpaquePacketFrustumBits From(in Frustum value) => new(
        StaticOpaquePacketPlaneBits.From(value.Left),
        StaticOpaquePacketPlaneBits.From(value.Right),
        StaticOpaquePacketPlaneBits.From(value.Bottom),
        StaticOpaquePacketPlaneBits.From(value.Top),
        StaticOpaquePacketPlaneBits.From(value.Near),
        StaticOpaquePacketPlaneBits.From(value.Far));
}

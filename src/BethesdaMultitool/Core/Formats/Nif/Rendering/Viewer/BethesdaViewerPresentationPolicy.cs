using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>One initial eye bearing for the native standalone scene viewer.</summary>
internal readonly record struct BethesdaViewerOrbitPreset(
    float AzimuthDegrees,
    float ElevationDegrees);

/// <summary>
///     Presentation defaults shared by the native mesh and actor viewer. Scene purpose is explicit
///     so actor-facing choices cannot accidentally change raw-NIF inspection or dedicated sky framing.
/// </summary>
internal static class BethesdaViewerPresentationPolicy
{
    private const float DefaultAssetAzimuthDegrees = 315f;
    private const float DefaultAssetElevationDegrees = 30f;

    private const float RawSkyElevationDegrees = -30f;

    // Bethesda actors face world +Y. The native camera measures compass bearing clockwise from +Y,
    // so its frontal eye is 0 degrees. Do not copy the sprite renderer's numeric 90-degree preset:
    // that renderer measures azimuth counter-clockwise from +X, making the same +Y eye read as 90.
    private const float ActorFrontAzimuthDegrees = 0f;
    private const float ActorFrontElevationDegrees = 0f;
    private const float OrbitDegreesPerPixel = 0.35f;

    private static readonly Vector4 DefaultAssetSceneClearColor =
        new(0.025f, 0.03f, 0.04f, 1f);

    /// <summary>
    ///     Scene-clear input that GammaAces maps to the legacy WebView's <c>#1a1a2e</c> display
    ///     background. The native scene target is HDR and is tonemapped before presentation, so
    ///     clearing it directly to 26/255, 26/255, 46/255 would make the final backdrop too dark.
    /// </summary>
    private static readonly Vector4 ActorGammaAcesSceneClearColor =
        new(0.14683776f, 0.14683776f, 0.21248831f, 1f);

    /// <summary>
    ///     Keeps the actor viewer's established purple/navy presentation without changing the raw
    ///     mesh viewer's existing neutral inspection backdrop.
    /// </summary>
    internal static Vector4 ResolveSceneClearColor(BethesdaViewerScenePurpose purpose)
    {
        return IsActorAppearance(purpose)
            ? ActorGammaAcesSceneClearColor
            : DefaultAssetSceneClearColor;
    }

    /// <summary>Resolves the initial orbit without inferring actor identity from asset names.</summary>
    internal static BethesdaViewerOrbitPreset ResolveInitialOrbit(
        BethesdaViewerScenePurpose purpose,
        bool dedicatedRawSky)
    {
        if (dedicatedRawSky)
        {
            return new BethesdaViewerOrbitPreset(
                DefaultAssetAzimuthDegrees,
                RawSkyElevationDegrees);
        }

        return IsActorAppearance(purpose)
            ? new BethesdaViewerOrbitPreset(
                ActorFrontAzimuthDegrees,
                ActorFrontElevationDegrees)
            : new BethesdaViewerOrbitPreset(
                DefaultAssetAzimuthDegrees,
                DefaultAssetElevationDegrees);
    }

    /// <summary>
    ///     Converts a pointer drag into the classic direct-manipulation orbit used by the former
    ///     WebView and the sister tools' Three.js OrbitControls. Dragging right moves the eye toward
    ///     screen-left so the model follows the pointer; dragging down moves the eye toward screen-up.
    ///     The purpose remains explicit so actor and raw-view contracts stay independent.
    /// </summary>
    internal static Vector2 OrbitDegreesForPointerDelta(
        Vector2 pixelDelta,
        BethesdaViewerScenePurpose purpose)
    {
        if (!float.IsFinite(pixelDelta.X) ||
            !float.IsFinite(pixelDelta.Y) ||
            !Enum.IsDefined(purpose))
        {
            return Vector2.Zero;
        }

        return new Vector2(
            pixelDelta.X * OrbitDegreesPerPixel,
            pixelDelta.Y * OrbitDegreesPerPixel);
    }

    private static bool IsActorAppearance(BethesdaViewerScenePurpose purpose)
    {
        return purpose is BethesdaViewerScenePurpose.NpcAppearance or
            BethesdaViewerScenePurpose.CreatureAppearance;
    }
}

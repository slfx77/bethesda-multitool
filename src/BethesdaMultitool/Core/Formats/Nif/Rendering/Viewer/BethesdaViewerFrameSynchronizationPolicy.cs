namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Keeps the native asset viewer's queue-drain decision explicit and independently testable.
///     Only the short placeholder-to-resident texture promotion window needs serialization; settled
///     scenes retain the normal two-frames-in-flight path.
/// </summary>
internal static class BethesdaViewerFrameSynchronizationPolicy
{
    internal static bool RequiresGpuIdleBeforeFrame(
        bool sessionReady,
        bool texturesSettled) =>
        sessionReady && !texturesSettled;
}

using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Transactional parse/name-binding result for one standalone KF payload. Clips and reports are
///     detached managed data; the binder never mutates the destination scene or retains source
///     bytes, filesystem handles, or callbacks.
/// </summary>
internal sealed record BethesdaViewerKfBindingResult(
    string SourceLabel,
    int SourceSequenceCount,
    IReadOnlyList<BethesdaViewerAnimationClip> AcceptedClips,
    IReadOnlyList<BethesdaViewerNameBindingReport> Reports,
    string Summary)
{
    internal bool HasAcceptedClips => AcceptedClips.Count > 0;
}

/// <summary>
///     Shared standalone-KF parser/binder used by both the manual picker and model-family catalog.
///     Keeping the operation transactional lets callers parse on a worker thread and publish the
///     accepted clips on the UI thread only after their scene/generation gates still match.
/// </summary>
internal static class BethesdaViewerKfAnimationBinder
{
    internal const long MaximumPayloadBytes = 64L * 1024L * 1024L;

    internal static BethesdaViewerKfBindingResult ParseAndBind(
        byte[] data,
        BethesdaViewerScene scene,
        string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);

        var nif = NifParser.Parse(data);
        var sources = nif is null
            ? []
            : NifControllerSequenceNameTrackReader.ReadAll(data, nif);
        return Bind(scene, sources, sourceLabel);
    }

    /// <summary>Testable name-binding boundary after the binary reader has produced sequences.</summary>
    internal static BethesdaViewerKfBindingResult Bind(
        BethesdaViewerScene scene,
        IReadOnlyList<NifNameTargetedAnimationClip> sources,
        string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);

        if (sources.Count == 0)
        {
            return new BethesdaViewerKfBindingResult(
                sourceLabel,
                0,
                [],
                [],
                "No supported controller sequence was found. Supported KF layouts are " +
                "Oblivion 20.0.0.4/.5 BS11 and Bethesda 20.2.0.7 BS streams.");
        }

        var suppressAccumulatedRootMotion = scene.Purpose is
            BethesdaViewerScenePurpose.NpcAppearance or
            BethesdaViewerScenePurpose.CreatureAppearance;
        var accepted = new List<BethesdaViewerAnimationClip>(sources.Count);
        var reports = new List<BethesdaViewerNameBindingReport>(sources.Count);
        foreach (var source in sources)
        {
            var clip = BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(
                scene,
                source,
                suppressAccumulatedRootMotion,
                out var report);
            reports.Add(report);
            if (clip is not null)
            {
                accepted.Add(clip);
            }
        }

        var unsupported = reports.Sum(static report => report.UnsupportedTransformTrackCount);
        var unbound = reports.Sum(static report =>
            report.MissingTargetTrackCount +
            report.AmbiguousTargetTrackCount +
            report.DuplicateSourceTrackCount +
            report.DestinationCollisionTrackCount);
        var suppressed = reports.Sum(static report => report.SuppressedAccumRootTrackCount);

        string summary;
        if (accepted.Count == 0)
        {
            summary = reports
                .Select(static report => report.FailureReason)
                .FirstOrDefault(static reason => !string.IsNullOrWhiteSpace(reason)) ??
                "No KF track bound uniquely to this scene's nodes.";
        }
        else
        {
            summary = $"Loaded {accepted.Count}/{sources.Count} sequence(s) from {sourceLabel}.";
        }

        if (unsupported > 0)
        {
            summary += accepted.Count == 0
                ? $" {unsupported} BSpline/unsupported transform track(s) cannot be played."
                : $" {unsupported} BSpline/unsupported transform track(s) are not played.";
        }

        if (unbound > 0)
        {
            summary += $" {unbound} non-unique or missing target track(s) were skipped.";
        }

        if (suppressed > 0)
        {
            summary += $" {suppressed} accumulated-root track(s) were suppressed.";
        }

        return new BethesdaViewerKfBindingResult(
            sourceLabel,
            sources.Count,
            accepted.ToArray(),
            reports.ToArray(),
            summary);
    }
}

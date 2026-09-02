using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>Outcome census for one standalone-KF name-binding attempt.</summary>
internal readonly record struct BethesdaViewerNameBindingReport(
    int SourceTrackCount,
    int BoundTrackCount,
    int MissingTargetTrackCount,
    int AmbiguousTargetTrackCount,
    int DuplicateSourceTrackCount,
    int DestinationCollisionTrackCount,
    int SuppressedAccumRootTrackCount,
    int UnsupportedTransformTrackCount,
    string? FailureReason);

/// <summary>
///     Transactionally binds a standalone KF's name-targeted tracks to one viewer scene. The
///     adapter never mutates <see cref="BethesdaViewerScene.AnimationClips" />; the caller appends a
///     successfully validated return value, so partial or ambiguous binding cannot leak into a live
///     scene.
/// </summary>
internal static class BethesdaViewerNameTargetedAnimationAdapter
{
    internal static BethesdaViewerAnimationClip? TryCreateClip(
        BethesdaViewerScene scene,
        NifNameTargetedAnimationClip source,
        bool suppressAccumulatedRootMotion,
        out BethesdaViewerNameBindingReport report)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(source);

        var sourceTrackCount = source.Tracks?.Length ?? 0;
        var unsupportedCount = source.UnsupportedTransformTrackCount;
        var missing = 0;
        var ambiguous = 0;
        var duplicateSource = 0;
        var destinationCollision = 0;
        var suppressed = 0;

        BethesdaViewerAnimationClip? Fail(
            string reason,
            out BethesdaViewerNameBindingReport failureReport)
        {
            failureReport = new BethesdaViewerNameBindingReport(
                sourceTrackCount,
                0,
                missing,
                ambiguous,
                duplicateSource,
                destinationCollision,
                suppressed,
                Math.Max(unsupportedCount, 0),
                reason);
            return null;
        }

        if (string.IsNullOrWhiteSpace(source.Name) || source.Tracks is null ||
            source.TextKeys is null || unsupportedCount < 0)
        {
            return Fail("The KF clip metadata is malformed.", out report);
        }

        if (source.Cycle == NifCycleType.Reverse)
        {
            return Fail(
                "Reverse/ping-pong KF cycles are not representable by the viewer clip contract.",
                out report);
        }

        if (source.Cycle is not (NifCycleType.Loop or NifCycleType.Clamp))
        {
            return Fail("The KF clip has an unknown cycle mode.", out report);
        }

        var effectiveFrequency = source.Frequency == 0f ? 1f : source.Frequency;
        if (!float.IsFinite(effectiveFrequency) || effectiveFrequency <= 0f ||
            !float.IsFinite(source.StartTime) ||
            !TryNormalizeTime(
                source.StopTime,
                source.StartTime,
                effectiveFrequency,
                out var stopTime) ||
            stopTime <= 0f)
        {
            return Fail("The KF clip clock is malformed.", out report);
        }

        const float startTime = 0f;

        var duplicateNames = source.Tracks
            .Where(static track => track is not null && !string.IsNullOrWhiteSpace(track.NodeName))
            .GroupBy(static track => track.NodeName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<BindingCandidate>(source.Tracks.Length);
        foreach (var track in source.Tracks)
        {
            if (track is null || string.IsNullOrWhiteSpace(track.NodeName))
            {
                return Fail("The KF clip contains an unnamed or null transform track.", out report);
            }

            if (suppressAccumulatedRootMotion && IsAccumulatedRootTarget(
                    track.NodeName,
                    source.AccumRootName))
            {
                suppressed++;
                continue;
            }

            if (duplicateNames.Contains(track.NodeName))
            {
                duplicateSource++;
                continue;
            }

            var resolution = ResolveUniqueNode(scene, track.NodeName);
            if (resolution.MatchCount == 0)
            {
                missing++;
                continue;
            }

            if (resolution.MatchCount != 1)
            {
                ambiguous++;
                continue;
            }

            candidates.Add(new BindingCandidate(resolution.NodeIndex, track));
        }

        var collidedNodes = candidates
            .GroupBy(static candidate => candidate.NodeIndex)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet();
        destinationCollision = candidates.Count(candidate => collidedNodes.Contains(candidate.NodeIndex));

        var boundTracks = new List<BethesdaViewerNodeAnimationTrack>(
            candidates.Count - destinationCollision);
        foreach (var candidate in candidates)
        {
            if (collidedNodes.Contains(candidate.NodeIndex))
            {
                continue;
            }

            if (!TryConvertTrack(
                    candidate.Track,
                    candidate.NodeIndex,
                    source.StartTime,
                    effectiveFrequency,
                    out var converted))
            {
                return Fail(
                    $"KF target '{candidate.Track.NodeName}' contains malformed key data.",
                    out report);
            }

            boundTracks.Add(converted);
        }

        if (boundTracks.Count == 0)
        {
            return Fail(
                "The KF clip has no uniquely bound supported transform tracks.",
                out report);
        }

        var textKeys = new BethesdaViewerTextKey[source.TextKeys.Length];
        for (var index = 0; index < source.TextKeys.Length; index++)
        {
            var sourceKey = source.TextKeys[index];
            if (string.IsNullOrWhiteSpace(sourceKey.Label) ||
                !TryNormalizeTime(
                    sourceKey.Time,
                    source.StartTime,
                    effectiveFrequency,
                    out var keyTime))
            {
                return Fail("The KF clip contains malformed text keys.", out report);
            }

            textKeys[index] = new BethesdaViewerTextKey(keyTime, sourceKey.Label);
        }

        var clip = new BethesdaViewerAnimationClip(
            source.Name,
            startTime,
            stopTime,
            source.Cycle == NifCycleType.Loop,
            boundTracks.ToArray(),
            [],
            textKeys);
        if (!BethesdaViewerAnimationValidator.TryValidate(
                clip,
                scene.Nodes.Count,
                scene.MeshParts.Count,
                out var validationError))
        {
            return Fail($"The bound KF clip is invalid: {validationError}", out report);
        }

        report = new BethesdaViewerNameBindingReport(
            sourceTrackCount,
            boundTracks.Count,
            missing,
            ambiguous,
            duplicateSource,
            destinationCollision,
            suppressed,
            unsupportedCount,
            null);
        return clip;
    }

    private static bool TryConvertTrack(
        NifNodeTrack source,
        int nodeIndex,
        float sequenceStartTime,
        float frequency,
        out BethesdaViewerNodeAnimationTrack converted)
    {
        converted = null!;
        if (!float.IsFinite(source.Frequency) || !float.IsFinite(source.Phase) ||
            !TryConvert(source.RotationInterpolation, out var rotationInterpolation) ||
            !TryConvert(source.TranslationInterpolation, out var translationInterpolation) ||
            !TryConvert(source.ScaleInterpolation, out var scaleInterpolation) ||
            !TryConvert(source.RotationKeys, sequenceStartTime, frequency, out var rotationKeys) ||
            !TryConvert(source.TranslationKeys, sequenceStartTime, frequency, out var translationKeys) ||
            !TryConvert(source.ScaleKeys, sequenceStartTime, frequency, out var scaleKeys) ||
            !TryConvertOptional(
                source.EulerXKeys,
                sequenceStartTime,
                frequency,
                out var eulerXKeys) ||
            !TryConvertOptional(
                source.EulerYKeys,
                sequenceStartTime,
                frequency,
                out var eulerYKeys) ||
            !TryConvertOptional(
                source.EulerZKeys,
                sequenceStartTime,
                frequency,
                out var eulerZKeys))
        {
            return false;
        }

        // Sequence frequency is the outer KF clock. Times were normalized into wall seconds above,
        // so each destination track deliberately carries an identity clock.
        converted = new BethesdaViewerNodeAnimationTrack(
            nodeIndex,
            1f,
            0f,
            rotationInterpolation,
            rotationKeys,
            translationInterpolation,
            translationKeys,
            scaleInterpolation,
            scaleKeys,
            eulerXKeys,
            eulerYKeys,
            eulerZKeys);
        return true;
    }

    private static bool TryConvert(
        NifKeyInterpolation source,
        out BethesdaViewerKeyInterpolation converted)
    {
        converted = source switch
        {
            NifKeyInterpolation.Linear => BethesdaViewerKeyInterpolation.Linear,
            NifKeyInterpolation.Quadratic => BethesdaViewerKeyInterpolation.Quadratic,
            NifKeyInterpolation.Tbc => BethesdaViewerKeyInterpolation.Tbc,
            NifKeyInterpolation.XyzEuler => BethesdaViewerKeyInterpolation.XyzEuler,
            NifKeyInterpolation.Constant => BethesdaViewerKeyInterpolation.Constant,
            _ => default
        };
        return source is NifKeyInterpolation.Linear or
            NifKeyInterpolation.Quadratic or
            NifKeyInterpolation.Tbc or
            NifKeyInterpolation.XyzEuler or
            NifKeyInterpolation.Constant;
    }

    private static bool TryConvert(
        NifQuatKey[] source,
        float sequenceStartTime,
        float frequency,
        out BethesdaViewerQuaternionKey[] converted)
    {
        converted = new BethesdaViewerQuaternionKey[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            if (!TryNormalizeTime(
                    source[index].Time,
                    sequenceStartTime,
                    frequency,
                    out var time))
            {
                converted = [];
                return false;
            }

            converted[index] = new BethesdaViewerQuaternionKey(time, source[index].Value);
        }

        return true;
    }

    private static bool TryConvert(
        NifVec3Key[] source,
        float sequenceStartTime,
        float frequency,
        out BethesdaViewerVector3Key[] converted)
    {
        converted = new BethesdaViewerVector3Key[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            if (!TryNormalizeTime(
                    source[index].Time,
                    sequenceStartTime,
                    frequency,
                    out var time))
            {
                converted = [];
                return false;
            }

            converted[index] = new BethesdaViewerVector3Key(time, source[index].Value);
        }

        return true;
    }

    private static bool TryConvert(
        NifFloatKey[] source,
        float sequenceStartTime,
        float frequency,
        out BethesdaViewerFloatKey[] converted)
    {
        converted = new BethesdaViewerFloatKey[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            if (!TryNormalizeTime(
                    source[index].Time,
                    sequenceStartTime,
                    frequency,
                    out var time))
            {
                converted = [];
                return false;
            }

            converted[index] = new BethesdaViewerFloatKey(time, source[index].Value);
        }

        return true;
    }

    private static bool TryConvertOptional(
        NifFloatKey[]? source,
        float sequenceStartTime,
        float frequency,
        out BethesdaViewerFloatKey[]? converted)
    {
        if (source is null)
        {
            converted = null;
            return true;
        }

        var success = TryConvert(source, sequenceStartTime, frequency, out var values);
        converted = success ? values : null;
        return success;
    }

    private static bool TryNormalizeTime(
        float authoredTime,
        float sequenceStartTime,
        float frequency,
        out float wallTime)
    {
        var normalized = ((double)authoredTime - sequenceStartTime) / frequency;
        if (!float.IsFinite(authoredTime) || !float.IsFinite(sequenceStartTime) ||
            !double.IsFinite(normalized) ||
            normalized is > float.MaxValue or < -float.MaxValue)
        {
            wallTime = 0f;
            return false;
        }

        wallTime = (float)normalized;
        return true;
    }

    private static NodeResolution ResolveUniqueNode(BethesdaViewerScene scene, string targetName)
    {
        var nodeIndex = -1;
        var matchCount = 0;
        for (var index = 0; index < scene.Nodes.Count; index++)
        {
            var node = scene.Nodes[index];
            if (!string.Equals(node.LookupName, targetName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(node.Name, targetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nodeIndex = index;
            matchCount++;
        }

        return new NodeResolution(nodeIndex, matchCount);
    }

    private static bool IsAccumulatedRootTarget(string targetName, string? accumRootName)
    {
        if (string.IsNullOrWhiteSpace(accumRootName))
        {
            return false;
        }

        if (string.Equals(targetName, accumRootName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetName, accumRootName + " NonAccum", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var pelvisVariant = accumRootName.Replace(
            "Pelvis",
            "NonAccum",
            StringComparison.OrdinalIgnoreCase);
        return !string.Equals(pelvisVariant, accumRootName, StringComparison.Ordinal) &&
               string.Equals(targetName, pelvisVariant, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct BindingCandidate(int NodeIndex, NifNodeTrack Track);

    private readonly record struct NodeResolution(int NodeIndex, int MatchCount);
}

using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Binds the established NIF controller-track model to stable native-viewer node indices.
///     Embedded NIF animation carries the source block index for every rig bone; that identity is
///     authoritative when duplicate node names occur. Name matching is deliberately accepted only
///     when it resolves to one scene node.
/// </summary>
internal static class BethesdaViewerNifAnimationAdapter
{
    internal static BethesdaViewerAnimationClip? TryCreateClip(
        BethesdaViewerScene scene,
        NifMeshAnimation animation,
        string? clipName = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(animation);
        if (animation.Bones.Length != animation.Tracks.Length ||
            !float.IsFinite(animation.ClipStart) ||
            !float.IsFinite(animation.ClipStop) ||
            animation.ClipStop <= animation.ClipStart)
        {
            return null;
        }

        var boundTracks = new List<BethesdaViewerNodeAnimationTrack>();
        var claimedNodes = new HashSet<int>();
        for (var boneIndex = 0; boneIndex < animation.Bones.Length; boneIndex++)
        {
            var track = animation.Tracks[boneIndex];
            if (track is null || !track.HasAnyKeys)
            {
                continue;
            }
            if (!TryConvert(track.RotationInterpolation, out var rotationInterpolation) ||
                !TryConvert(track.TranslationInterpolation, out var translationInterpolation) ||
                !TryConvert(track.ScaleInterpolation, out var scaleInterpolation))
            {
                return null;
            }

            var nodeIndex = ResolveNodeIndex(scene, animation.Bones[boneIndex], track.NodeName);
            if (nodeIndex < 0 || !claimedNodes.Add(nodeIndex))
            {
                continue;
            }

            boundTracks.Add(new BethesdaViewerNodeAnimationTrack(
                nodeIndex,
                track.Frequency,
                track.Phase,
                rotationInterpolation,
                track.RotationKeys
                    .Select(static key => new BethesdaViewerQuaternionKey(key.Time, key.Value))
                    .ToArray(),
                translationInterpolation,
                track.TranslationKeys
                    .Select(static key => new BethesdaViewerVector3Key(key.Time, key.Value))
                    .ToArray(),
                scaleInterpolation,
                track.ScaleKeys
                    .Select(static key => new BethesdaViewerFloatKey(key.Time, key.Value))
                    .ToArray(),
                Convert(track.EulerXKeys),
                Convert(track.EulerYKeys),
                Convert(track.EulerZKeys)));
        }

        if (boundTracks.Count == 0)
        {
            return null;
        }

        var clip = new BethesdaViewerAnimationClip(
            string.IsNullOrWhiteSpace(clipName) ? "Embedded Idle" : clipName,
            animation.ClipStart,
            animation.ClipStop,
            animation.ClipLoops,
            boundTracks.ToArray(),
            [],
            animation.TextKeys
                .Select(static key => new BethesdaViewerTextKey(key.Time, key.Label))
                .ToArray());
        return BethesdaViewerAnimationValidator.TryValidate(
            clip,
            scene.Nodes.Count,
            scene.MeshParts.Count,
            out _)
            ? clip
            : null;
    }

    private static int ResolveNodeIndex(
        BethesdaViewerScene scene,
        NifAnimBone bone,
        string trackNodeName)
    {
        if (bone.SourceBlockIndex >= 0)
        {
            var exactMatch = -1;
            for (var nodeIndex = 0; nodeIndex < scene.Nodes.Count; nodeIndex++)
            {
                if (scene.Nodes[nodeIndex].SourceBlockIndex == bone.SourceBlockIndex)
                {
                    if (exactMatch >= 0)
                    {
                        return -1;
                    }

                    exactMatch = nodeIndex;
                }
            }

            return exactMatch;
        }

        var match = -1;
        for (var nodeIndex = 0; nodeIndex < scene.Nodes.Count; nodeIndex++)
        {
            var node = scene.Nodes[nodeIndex];
            if (!string.Equals(node.LookupName, trackNodeName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(node.LookupName, bone.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(node.Name, trackNodeName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(node.Name, bone.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A duplicate name without source-block identity is ambiguous. Refuse the whole target
            // instead of whichever node happened to be appended first.
            if (match >= 0)
            {
                return -1;
            }

            match = nodeIndex;
        }

        return match;
    }

    private static bool TryConvert(
        NifKeyInterpolation interpolation,
        out BethesdaViewerKeyInterpolation converted)
    {
        converted = interpolation switch
        {
            NifKeyInterpolation.Linear => BethesdaViewerKeyInterpolation.Linear,
            NifKeyInterpolation.Quadratic => BethesdaViewerKeyInterpolation.Quadratic,
            NifKeyInterpolation.Tbc => BethesdaViewerKeyInterpolation.Tbc,
            NifKeyInterpolation.XyzEuler => BethesdaViewerKeyInterpolation.XyzEuler,
            NifKeyInterpolation.Constant => BethesdaViewerKeyInterpolation.Constant,
            _ => default
        };
        return interpolation is NifKeyInterpolation.Linear or
            NifKeyInterpolation.Quadratic or
            NifKeyInterpolation.Tbc or
            NifKeyInterpolation.XyzEuler or
            NifKeyInterpolation.Constant;
    }

    private static BethesdaViewerFloatKey[]? Convert(NifFloatKey[]? keys)
    {
        return keys?.Select(static key => new BethesdaViewerFloatKey(key.Time, key.Value)).ToArray();
    }
}

using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Bakes per-shape playback tracks for rigid node-animated NIFs — controller-driven nodes whose
///     geometry carries NO skin (the Goodsprings saloon sign, mill wheels). For every animated bone
///     in the collected rig, the full clip is sampled at a fixed rate into model-space deltas from
///     the rest pose, and every shape block in that node's subtree inherits the track (nearest
///     animated ancestor wins for nested animated nodes). The decoder attaches the result to each
///     submesh by <c>SourceBlockIndex</c>, and the renderer pre-multiplies instance worlds with the
///     evaluated delta — the same application shape as physics-lite sway.
///     <para>
///         Ancestors OUTSIDE the collected rig are treated as identity, exactly like the rig
///         builder does. This matches the shipped accum-root layout (animated nodes sit under an
///         identity NonAccum node); a non-identity out-of-rig ancestor would conjugate the delta
///         and is not handled.
///     </para>
/// </summary>
internal static class NifRigidNodeAnimationBaker
{
    private const float SampleRate = 30f;
    private const int MaxSamples = 600;

    internal static IReadOnlyDictionary<int, NifRigidNodeAnimation>? Bake(
        byte[] data, NifInfo nif, NifMeshAnimation animation)
    {
        var bones = animation.Bones;
        var clipLength = animation.ClipLength;
        if (bones.Length == 0 || !float.IsFinite(clipLength) || clipLength <= 0f)
        {
            return null;
        }

        // Rest-pose model-space worlds (tracks ignored) — the reference frame the deltas remove.
        var restWorlds = new Matrix4x4[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            var local = Matrix4x4.CreateFromQuaternion(bone.RestRotation) *
                        Matrix4x4.CreateScale(bone.RestScale);
            local.Translation = bone.RestTranslation;
            restWorlds[i] = bone.ParentIndex >= 0 ? local * restWorlds[bone.ParentIndex] : local;
        }

        var sampleCount = Math.Clamp((int)MathF.Round(clipLength * SampleRate) + 1, 2, MaxSamples);
        var effectiveRate = (sampleCount - 1) / clipLength;
        var boneWorlds = new Matrix4x4[bones.Length];

        // Sample every animated bone across the clip in one evaluator pass per time step.
        var animated = new List<int>();
        for (var i = 0; i < bones.Length; i++)
        {
            if (animation.Tracks[i] is { HasMotion: true } && bones[i].SourceBlockIndex >= 0)
            {
                animated.Add(i);
            }
        }

        if (animated.Count == 0)
        {
            return null;
        }

        var rotations = new Dictionary<int, Quaternion[]>(animated.Count);
        var translations = new Dictionary<int, Vector3[]>(animated.Count);
        var scales = new Dictionary<int, Vector3[]>(animated.Count);
        foreach (var boneIndex in animated)
        {
            rotations[boneIndex] = new Quaternion[sampleCount];
            translations[boneIndex] = new Vector3[sampleCount];
            scales[boneIndex] = new Vector3[sampleCount];
        }

        var degenerate = new HashSet<int>();
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var time = animation.ClipStart + sample / effectiveRate;
            NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, time, boneWorlds);
            foreach (var boneIndex in animated)
            {
                if (!Matrix4x4.Invert(restWorlds[boneIndex], out var inverseRest))
                {
                    degenerate.Add(boneIndex);
                    continue;
                }

                // Row-vector: baked model-space vertex × (rest⁻¹ × anim) lands on the animated pose.
                var delta = inverseRest * boneWorlds[boneIndex];
                if (!Matrix4x4.Decompose(delta, out var scale, out var rotation, out var translation))
                {
                    degenerate.Add(boneIndex);
                    continue;
                }

                rotations[boneIndex][sample] = rotation;
                translations[boneIndex][sample] = translation;
                scales[boneIndex][sample] = scale;
            }
        }

        // Depth-ordered subtree claim: shallower animated nodes first, deeper ones overwrite, so
        // each shape follows its NEAREST animated ancestor.
        var nodeChildren = ReadNodeChildren(data, nif);
        var depths = ComputeDepths(nif.Blocks.Count, nodeChildren);
        var tracksByShapeBlock = new Dictionary<int, NifRigidNodeAnimation>();
        foreach (var boneIndex in animated
                     .Where(index => !degenerate.Contains(index))
                     .OrderBy(index => depths.GetValueOrDefault(bones[index].SourceBlockIndex, 0)))
        {
            var track = new NifRigidNodeAnimation(
                animation.ClipStart,
                clipLength,
                animation.ClipLoops,
                effectiveRate,
                rotations[boneIndex],
                translations[boneIndex],
                scales[boneIndex]);
            foreach (var block in CollectSubtree(bones[boneIndex].SourceBlockIndex, nif.Blocks.Count, nodeChildren))
            {
                tracksByShapeBlock[block] = track;
            }
        }

        return tracksByShapeBlock.Count > 0 ? tracksByShapeBlock : null;
    }

    private static Dictionary<int, List<int>> ReadNodeChildren(byte[] data, NifInfo nif)
    {
        var result = new Dictionary<int, List<int>>();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var block = nif.Blocks[i];
            if (!NifSceneGraphWalker.NodeTypes.Contains(block.TypeName))
            {
                continue;
            }

            var children = NifBlockParsers.ParseNodeChildren(
                data, block, nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
            if (children is not null)
            {
                result[i] = children;
            }
        }

        return result;
    }

    private static Dictionary<int, int> ComputeDepths(
        int blockCount, Dictionary<int, List<int>> nodeChildren)
    {
        var hasParent = new HashSet<int>();
        foreach (var children in nodeChildren.Values)
        {
            foreach (var child in children)
            {
                hasParent.Add(child);
            }
        }

        var depths = new Dictionary<int, int>();
        var pending = new Queue<int>();
        foreach (var node in nodeChildren.Keys)
        {
            if (!hasParent.Contains(node))
            {
                depths[node] = 0;
                pending.Enqueue(node);
            }
        }

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!nodeChildren.TryGetValue(current, out var children))
            {
                continue;
            }

            var childDepth = depths[current] + 1;
            foreach (var child in children)
            {
                if (child < 0 || child >= blockCount || depths.ContainsKey(child))
                {
                    continue;
                }

                depths[child] = childDepth;
                pending.Enqueue(child);
            }
        }

        return depths;
    }

    private static List<int> CollectSubtree(
        int root, int blockCount, Dictionary<int, List<int>> nodeChildren)
    {
        var result = new List<int>();
        if (root < 0 || root >= blockCount)
        {
            return result;
        }

        var visited = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (current < 0 || current >= blockCount || !visited.Add(current))
            {
                continue;
            }

            result.Add(current);
            if (nodeChildren.TryGetValue(current, out var children))
            {
                foreach (var child in children)
                {
                    pending.Enqueue(child);
                }
            }
        }

        return result;
    }
}

using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Evaluates a <see cref="NifMeshAnimation" /> at a point in time into per-bone world matrices
///     and skin matrices. Pure math, deterministic, zero allocation (callers own the spans) — used
///     per frame by the viewer's CPU skinner and directly by tests.
/// </summary>
internal static class NifAnimationPoseEvaluator
{
    /// <summary>
    ///     Composes each bone's world transform at <paramref name="time" /> (already clip-mapped by
    ///     the caller, or raw key time for tests). Per CHANNEL, an animated track value replaces the
    ///     bone's rest value; channels without keys keep rest — mirroring the walker's per-node
    ///     override composition (<c>NifSceneGraphWalker.WalkNode</c>). Bones are ordered
    ///     parents-before-children, so one forward pass suffices; row-vector convention
    ///     (<c>world = local × parentWorld</c>) matches the scene walker.
    /// </summary>
    internal static void EvaluateBoneWorlds(NifMeshAnimation animation, float time, Span<Matrix4x4> boneWorlds)
    {
        var bones = animation.Bones;
        if (boneWorlds.Length < bones.Length)
        {
            throw new ArgumentException("boneWorlds span too small", nameof(boneWorlds));
        }

        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            var track = animation.Tracks[i];

            var rotation = bone.RestRotation;
            var translation = bone.RestTranslation;
            var scale = bone.RestScale;
            if (track is not null)
            {
                var trackTime = NifTrackSampler.MapTime(
                    time, track.Frequency, track.Phase, animation.ClipStart, animation.ClipStop,
                    animation.ClipLoops);
                if (track.RotationKeys.Length > 0)
                {
                    rotation = NifTrackSampler.SampleRotation(track.RotationKeys, trackTime);
                }
                else if (track.HasEulerRotation)
                {
                    rotation = NifTrackSampler.SampleEulerRotation(track, trackTime);
                }

                if (track.TranslationKeys.Length > 0)
                {
                    translation = NifTrackSampler.SampleTranslation(track.TranslationKeys, trackTime);
                }

                if (track.ScaleKeys.Length > 0)
                {
                    scale = NifTrackSampler.SampleScale(track.ScaleKeys, trackTime);
                }
            }

            var local = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateScale(scale);
            local.Translation = translation;

            boneWorlds[i] = bone.ParentIndex >= 0
                ? local * boneWorlds[bone.ParentIndex]
                : local;
        }
    }

    /// <summary>
    ///     Skin matrices from evaluated bone worlds: <c>skin[j] = inverseBind[j] × boneWorld[map[j]]</c>
    ///     — exactly the composition <c>NifShapeSkinningDataBuilder.BuildBoneSkinMatrices</c> bakes
    ///     for the static path. An unresolved mapping (−1) falls back to the identity SKIN matrix,
    ///     which leaves the vertex at its authored (bind-pose) position.
    /// </summary>
    internal static void ComputeSkinMatrices(
        Matrix4x4[] inverseBindPoses,
        int[] skinBoneToAnimBone,
        ReadOnlySpan<Matrix4x4> boneWorlds,
        Span<Matrix4x4> skinMatrices)
    {
        if (skinMatrices.Length < inverseBindPoses.Length)
        {
            throw new ArgumentException("skinMatrices span too small", nameof(skinMatrices));
        }

        for (var j = 0; j < inverseBindPoses.Length; j++)
        {
            var animBone = j < skinBoneToAnimBone.Length ? skinBoneToAnimBone[j] : -1;
            skinMatrices[j] = animBone >= 0 && animBone < boneWorlds.Length
                ? inverseBindPoses[j] * boneWorlds[animBone]
                : Matrix4x4.Identity;
        }
    }
}

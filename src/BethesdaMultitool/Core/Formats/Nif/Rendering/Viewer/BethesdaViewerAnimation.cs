using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Key interpolation basis as authored. Quadratic and TBC remain explicit so a renderer can
///     expose an approximation without erasing which higher-order basis the source requested.
/// </summary>
internal enum BethesdaViewerKeyInterpolation : byte
{
    Linear = 1,
    Quadratic = 2,
    Tbc = 3,
    XyzEuler = 4,
    Constant = 5
}

internal readonly record struct BethesdaViewerQuaternionKey(float Time, Quaternion Value);

internal readonly record struct BethesdaViewerVector3Key(float Time, Vector3 Value);

internal readonly record struct BethesdaViewerFloatKey(float Time, float Value);

internal readonly record struct BethesdaViewerTextKey(float Time, string Label);

/// <summary>
///     One node's local S/R/T channels. The node's static local transform supplies any channel
///     with no keys; the stable node index avoids ambiguous source names in duplicated skeletons.
/// </summary>
internal sealed record BethesdaViewerNodeAnimationTrack(
    int NodeIndex,
    float Frequency,
    float Phase,
    BethesdaViewerKeyInterpolation RotationInterpolation,
    BethesdaViewerQuaternionKey[] RotationKeys,
    BethesdaViewerKeyInterpolation TranslationInterpolation,
    BethesdaViewerVector3Key[] TranslationKeys,
    BethesdaViewerKeyInterpolation ScaleInterpolation,
    BethesdaViewerFloatKey[] ScaleKeys,
    BethesdaViewerFloatKey[]? EulerXKeys = null,
    BethesdaViewerFloatKey[]? EulerYKeys = null,
    BethesdaViewerFloatKey[]? EulerZKeys = null)
{
    internal bool HasEulerRotation =>
        EulerXKeys is { Length: > 0 } ||
        EulerYKeys is { Length: > 0 } ||
        EulerZKeys is { Length: > 0 };
}

/// <summary>One sampled set of named morph-target weights.</summary>
internal sealed record BethesdaViewerMorphWeightKey(float Time, float[] Weights);

/// <summary>
///     Morph-weight animation for one mesh part. <see cref="TargetNames" /> and every key's weight
///     array use the same order; source adapters can therefore retain FaceGen/expression channel
///     identity without baking the active pose into a replacement material or mesh contract.
/// </summary>
internal sealed record BethesdaViewerMorphWeightTrack(
    int MeshPartIndex,
    string[] TargetNames,
    float Frequency,
    float Phase,
    BethesdaViewerKeyInterpolation Interpolation,
    BethesdaViewerMorphWeightKey[] Keys);

/// <summary>
///     One independently selectable named clip. A scene may carry multiple controller sequences,
///     including node animation and facial/body morph channels, without choosing one at decode time.
/// </summary>
internal sealed record BethesdaViewerAnimationClip(
    string Name,
    float StartTime,
    float EndTime,
    bool Loops,
    BethesdaViewerNodeAnimationTrack[] NodeTracks,
    BethesdaViewerMorphWeightTrack[] MorphWeightTracks,
    BethesdaViewerTextKey[] TextKeys)
{
    internal float Duration => EndTime - StartTime;
}

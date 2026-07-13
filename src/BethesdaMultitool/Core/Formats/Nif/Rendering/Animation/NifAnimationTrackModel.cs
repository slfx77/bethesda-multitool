using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     NIF key interpolation basis, as authored (nif.xml KeyType). The runtime sampler substitutes
///     slerp/lerp as labeled stand-ins for the Quadratic/Tbc bases — the authored type is preserved
///     here so the approximation stays visible and revisitable.
/// </summary>
internal enum NifKeyInterpolation : byte
{
    Linear = 1,
    Quadratic = 2,
    Tbc = 3,
    XyzEuler = 4,
    Constant = 5,
}

internal readonly record struct NifQuatKey(float Time, Quaternion Value);

internal readonly record struct NifVec3Key(float Time, Vector3 Value);

internal readonly record struct NifFloatKey(float Time, float Value);

internal readonly record struct NifAnimTextKey(float Time, string Label);

/// <summary>
///     One animated node's keyframe tracks (from NiKeyframeData / NiTransformData) plus the timing
///     fields of the controller that drives it. Any channel may be empty — the node's authored rest
///     transform fills in per channel during pose evaluation.
/// </summary>
internal sealed record NifNodeTrack(
    string NodeName,
    float Frequency,
    float Phase,
    NifKeyInterpolation RotationInterpolation,
    NifQuatKey[] RotationKeys,
    NifKeyInterpolation TranslationInterpolation,
    NifVec3Key[] TranslationKeys,
    NifKeyInterpolation ScaleInterpolation,
    NifFloatKey[] ScaleKeys)
{
    public bool HasAnyKeys =>
        RotationKeys.Length > 0 || TranslationKeys.Length > 0 || ScaleKeys.Length > 0;

    /// <summary>True when any channel carries at least two keys — i.e. the track actually moves.</summary>
    public bool HasMotion =>
        RotationKeys.Length > 1 || TranslationKeys.Length > 1 || ScaleKeys.Length > 1;
}

/// <summary>
///     One bone in the flattened animation rig. Bones are ordered parents-before-children so a single
///     forward pass composes world transforms. Rest S/R/T is the node's authored local transform.
///     <see cref="SourceBlockIndex" /> is the NIF node block this bone came from — the skin exporter
///     maps skin-bone REFS through it (names alone are ambiguous: FNV's nv_ncr_flag_s carries two
///     node chains with identical names). Decode-time only: the disk cache does not persist it
///     (−1 after a cache round-trip; every consumer that needs it runs before serialization).
/// </summary>
internal sealed record NifAnimBone(
    string Name,
    int ParentIndex,
    Vector3 RestTranslation,
    Quaternion RestRotation,
    float RestScale,
    int SourceBlockIndex = -1);

/// <summary>
///     A mesh's complete animation description: the bone tree, per-bone keyframe tracks (parallel to
///     <see cref="Bones" />; null = static bone), the authored text keys, and the resolved play window.
///     Both the TES3 per-node NiKeyframeController graph and the modern
///     NiControllerManager/NiControllerSequence graph compile down to this one model — only the
///     collector differs.
/// </summary>
internal sealed record NifMeshAnimation(
    NifAnimBone[] Bones,
    NifNodeTrack?[] Tracks,
    NifAnimTextKey[] TextKeys,
    float ClipStart,
    float ClipStop,
    bool ClipLoops)
{
    public float ClipLength => ClipStop - ClipStart;
}

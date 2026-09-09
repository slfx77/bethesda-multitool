using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Exact degree-three, open-uniform B-spline payload retained from a transform interpolator.
///     Control points remain control points: they are evaluated against the authored basis at
///     runtime and are never presented as linear animation keys.
/// </summary>
internal sealed record NifBsplineTransformData(
    float StartTime,
    float StopTime,
    Vector3? DefaultTranslation,
    Quaternion? DefaultRotation,
    float? DefaultScale,
    Vector3[]? TranslationControlPoints,
    Quaternion[]? RotationControlPoints,
    float[]? ScaleControlPoints)
{
    internal int ControlPointCount =>
        TranslationControlPoints?.Length ??
        RotationControlPoints?.Length ??
        ScaleControlPoints?.Length ??
        0;

    internal bool HasAnyValue =>
        TranslationControlPoints is { Length: > 0 } ||
        RotationControlPoints is { Length: > 0 } ||
        ScaleControlPoints is { Length: > 0 } ||
        DefaultTranslation.HasValue ||
        DefaultRotation.HasValue ||
        DefaultScale.HasValue;
}

/// <summary>One standalone KF B-spline transform addressed by its destination node name.</summary>
internal sealed record NifNameTargetedBsplineTransformTrack(
    string NodeName,
    NifBsplineTransformData Transform);

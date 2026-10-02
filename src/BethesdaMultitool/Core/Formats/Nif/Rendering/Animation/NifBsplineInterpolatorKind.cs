namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>The value an NiBSpline*Interpolator drives, which fixes its static value and its channels.</summary>
internal enum NifBsplineInterpolatorKind : byte
{
    /// <summary>One float (NiBSplineFloatInterpolator, NiBSplineCompFloatInterpolator): one channel, "float".</summary>
    Float,

    /// <summary>A Vector3 (NiBSplinePoint3Interpolator, NiBSplineCompPoint3Interpolator): one channel, "position".</summary>
    Point3,

    /// <summary>
    ///     A transform (NiBSplineTransformInterpolator, NiBSplineCompTransformInterpolator): three channels, translation,
    ///     rotation and scale.
    /// </summary>
    Transform
}

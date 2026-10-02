namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless reading of one NiBSpline*Interpolator block (nif.xml NiBSplineInterpolator and its six 20.2.0.7
///     subclasses): every stored field as its raw bits, in file order. Read by
///     <see cref="NifBsplineTransformReader.TryReadInterpolatorView" />.
/// </summary>
/// <param name="TypeName">The block type the layout was chosen by.</param>
/// <param name="Kind">The value the interpolator drives.</param>
/// <param name="Compact">True for the Comp forms, which store an Offset and a Half Range per channel.</param>
/// <param name="StartTimeBits">The raw bits of Start Time.</param>
/// <param name="StopTimeBits">The raw bits of Stop Time.</param>
/// <param name="SplineDataRef">The Spline Data ref (NiBSplineData), as stored (-1 for none).</param>
/// <param name="BasisDataRef">The Basis Data ref (NiBSplineBasisData), as stored (-1 for none).</param>
/// <param name="StaticValueBits">
///     The static value's raw bits in file order: one float; a Vector3 x, y, z; or an NiQuatTransform's translation x, y,
///     z, rotation w, x, y, z and scale (8 words). An undriven channel's static value is usually the +FLT_MAX sentinel
///     (0x7F7FFFFF) or -FLT_MAX (0xFF7FFFFF), kept as stored.
/// </param>
/// <param name="Handles">
///     Each channel's Handle as stored (the first control scalar in the NiBSplineData array; 0xFFFF marks an absent
///     channel): one for Float and Point3, translation, rotation, scale for Transform.
/// </param>
/// <param name="OffsetBits">The Comp forms' per-channel Offset (the dequantization bias) raw bits; empty otherwise.</param>
/// <param name="HalfRangeBits">The Comp forms' per-channel Half Range (the multiplier) raw bits; empty otherwise.</param>
internal sealed record NifBsplineInterpolatorView(
    string TypeName,
    NifBsplineInterpolatorKind Kind,
    bool Compact,
    uint StartTimeBits,
    uint StopTimeBits,
    int SplineDataRef,
    int BasisDataRef,
    uint[] StaticValueBits,
    uint[] Handles,
    uint[] OffsetBits,
    uint[] HalfRangeBits)
{
    /// <summary>The number of channels (1, or 3 for a transform).</summary>
    public int ChannelCount => Handles.Length;
}

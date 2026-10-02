namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     An NiPoint3Interpolator (nif.xml: Value, Data) exactly as stored: the raw bits of the pose Value's three
///     components, which are #INV_VEC3# (three times -FLT_MAX, 0xFF7FFFFF) when the interpolator has no pose value, and
///     the Data ref (NiPosData). Read by <see cref="NifPropertyInterpolatorReader.TryReadPoint3InterpolatorView" />.
/// </summary>
/// <param name="XBits">The raw bits of the stored Value x.</param>
/// <param name="YBits">The raw bits of the stored Value y.</param>
/// <param name="ZBits">The raw bits of the stored Value z.</param>
/// <param name="DataRef">The Data ref as stored (-1 for none).</param>
internal readonly record struct NifPoint3InterpolatorView(uint XBits, uint YBits, uint ZBits, int DataRef)
{
    /// <summary>nif.xml's #INV_FLT#, -FLT_MAX: each component of #INV_VEC3#.</summary>
    public const uint InvalidValueBits = 0xFF7FFFFF;

    /// <summary>True when every stored component is the sentinel (the interpolator has no pose value).</summary>
    public bool HasNoValue => XBits == InvalidValueBits && YBits == InvalidValueBits && ZBits == InvalidValueBits;

    /// <summary>True when some but not all components are the sentinel (a shape nif.xml does not define).</summary>
    public bool MixesSentinel => !HasNoValue &&
                                 (XBits == InvalidValueBits || YBits == InvalidValueBits || ZBits == InvalidValueBits);
}

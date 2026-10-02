namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     An NiFloatInterpolator (nif.xml: Value, Data) exactly as stored: the raw bits of the pose Value, which is
///     #INV_FLT# (-FLT_MAX, 0xFF7FFFFF) when the interpolator has no pose value, and the Data ref (NiFloatData). Read by
///     <see cref="NifGeomMorpherReader.TryReadFloatInterpolatorView" />.
/// </summary>
/// <param name="ValueBits">The raw bits of the stored Value.</param>
/// <param name="DataRef">The Data ref as stored (-1 for none).</param>
internal readonly record struct NifFloatInterpolatorView(uint ValueBits, int DataRef)
{
    /// <summary>nif.xml's #INV_FLT#, -FLT_MAX: the Value of an interpolator with no pose value.</summary>
    public const uint InvalidValueBits = 0xFF7FFFFF;

    /// <summary>True when the stored Value is the #INV_FLT# sentinel.</summary>
    public bool HasNoValue => ValueBits == InvalidValueBits;
}

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     An NiBoolInterpolator or NiBoolTimelineInterpolator (nif.xml: Value, Data) exactly as stored: the pose Value byte,
///     whose nif.xml default 2 means the interpolator has no pose value, and the Data ref (NiBoolData). Read by
///     <see cref="NifPropertyInterpolatorReader.TryReadBoolInterpolatorView" />.
/// </summary>
/// <param name="Value">The stored Value byte (0 false, 1 true, 2 no value; anything else is kept as stored).</param>
/// <param name="DataRef">The Data ref as stored (-1 for none).</param>
internal readonly record struct NifBoolInterpolatorView(byte Value, int DataRef)
{
    /// <summary>nif.xml's default Value of an interpolator with no pose value.</summary>
    public const byte NoValue = 2;

    /// <summary>True when the stored Value is a bool (0 or 1).</summary>
    public bool HasBinaryValue => Value <= 1;
}

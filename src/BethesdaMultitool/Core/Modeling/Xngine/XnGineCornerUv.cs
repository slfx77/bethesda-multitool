namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     One plane corner's u and v in 1/16-texel units: either the int16 values the record stores (the
///     <c>xngine.uv16</c> stream, plan decision D1) or the values the reference UV rule derives from them
///     (<see cref="XnGineUvRule" />).
/// </summary>
/// <param name="U">The u value, in 1/16 texel.</param>
/// <param name="V">The v value, in 1/16 texel.</param>
internal readonly record struct XnGineCornerUv(int U, int V);

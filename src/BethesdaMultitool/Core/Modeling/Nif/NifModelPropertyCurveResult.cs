using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One property interpolator mapped by <see cref="NifModelPropertyCurveMapping" /> (cut-1b slice 14): the Shared
///     curve at the kind's width that drives one <see cref="ScenePropertyTrack" />, or the reason it stays native.
/// </summary>
/// <param name="InterpolatorBlock">The interpolator block; -1 when the curve came from a data block with no interpolator (NiUVData).</param>
/// <param name="Blocks">The interpolator and the key or spline data blocks the decision about this curve covers.</param>
/// <param name="Curve">The curve; null when the interpolator stays native.</param>
/// <param name="KeyType">The stored key type the curve came from (1 LINEAR, 2 QUADRATIC, 3 TBC, 5 CONST), or 0 for a constant or a B-spline.</param>
/// <param name="HasCurve">True when the interpolator stores keys or a B-spline handle (RE-22 rule 3a needs it for the sentinel clock).</param>
/// <param name="NativeReason">Why the interpolator stays native; null when <paramref name="Curve" /> is set.</param>
/// <param name="NativeCode">The code of <paramref name="NativeReason" />; null when typed.</param>
internal sealed record NifModelPropertyCurveResult(
    int InterpolatorBlock,
    IReadOnlyList<int> Blocks,
    SceneCurve? Curve,
    uint KeyType,
    bool HasCurve,
    string? NativeReason,
    string? NativeCode)
{
    /// <summary>True when the interpolator carries a Shared curve.</summary>
    public bool IsTyped => Curve is not null;

    /// <summary>A typed curve.</summary>
    /// <param name="interpolatorBlock">The interpolator block, or -1.</param>
    /// <param name="blocks">The blocks the decision covers.</param>
    /// <param name="curve">The curve.</param>
    /// <param name="keyType">The stored key type, or 0.</param>
    /// <param name="hasCurve">Whether the interpolator stores keys or a spline handle.</param>
    /// <returns>The result.</returns>
    public static NifModelPropertyCurveResult Typed(int interpolatorBlock, IReadOnlyList<int> blocks, SceneCurve curve,
        uint keyType, bool hasCurve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return new NifModelPropertyCurveResult(interpolatorBlock, blocks, curve, keyType, hasCurve, null, null);
    }

    /// <summary>An interpolator that stays native.</summary>
    /// <param name="interpolatorBlock">The interpolator block, or -1.</param>
    /// <param name="blocks">The blocks the decision covers.</param>
    /// <param name="reason">The reason.</param>
    /// <param name="code">Its code.</param>
    /// <param name="hasCurve">Whether the interpolator stores keys or a spline handle (a blocked curve still counts).</param>
    /// <returns>The result.</returns>
    public static NifModelPropertyCurveResult Native(int interpolatorBlock, IReadOnlyList<int> blocks, string reason,
        string code, bool hasCurve = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new NifModelPropertyCurveResult(interpolatorBlock, blocks, null, 0, hasCurve, reason, code);
    }
}

using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One morph slot mapped by <see cref="NifModelAnimationMorphs.MapSlot" /> (cut-1b slice 7): the width-1 Shared curve
///     that drives the cut-1a target the morph maps to, or the reason the slot stays native. Morph j (1..n-1) drives target
///     j - 1, exactly as <see cref="NifModelMorphReader" /> numbers the targets; morph 0 (the Base) never reaches here.
/// </summary>
/// <param name="MorphIndex">The morph index in the NiMorphData (1 or more).</param>
/// <param name="InterpolatorBlock">The interpolator block that drives the slot; -1 when the stored item weight does.</param>
/// <param name="Blocks">The interpolator and the key or spline data blocks the decision about this slot covers (empty for a stored weight).</param>
/// <param name="Curve">The weight curve; null when the slot stays native.</param>
/// <param name="KeyCurve">
///     The slice-2 key curve behind <paramref name="Curve" /> when the slot is keyed by ordinary keys, which the
///     whole-vector merge compares; null for a Constant or a B-spline curve.
/// </param>
/// <param name="HasCurve">True when the slot stores keys or a B-spline handle (RE-22 rule 3a needs it for the sentinel clock).</param>
/// <param name="NativeReason">Why the slot stays native; null when <paramref name="Curve" /> is set.</param>
/// <param name="NativeCode">The code of <paramref name="NativeReason" />; null when typed.</param>
internal sealed record NifModelMorphTargetResult(
    int MorphIndex,
    int InterpolatorBlock,
    IReadOnlyList<int> Blocks,
    SceneCurve? Curve,
    NifModelCurve? KeyCurve,
    bool HasCurve,
    string? NativeReason,
    string? NativeCode)
{
    /// <summary>The cut-1a morph target the morph maps to (morph j is target j - 1).</summary>
    public int TargetIndex => MorphIndex - 1;

    /// <summary>True when the slot carries a Shared curve.</summary>
    public bool IsTyped => Curve is not null;

    /// <summary>A typed slot.</summary>
    /// <param name="morphIndex">The morph index (1 or more).</param>
    /// <param name="interpolatorBlock">The interpolator block, or -1 for a stored weight.</param>
    /// <param name="blocks">The blocks the decision covers.</param>
    /// <param name="curve">The weight curve.</param>
    /// <param name="keyCurve">The key curve behind it, or null.</param>
    /// <param name="hasCurve">Whether the slot stores keys or a spline handle.</param>
    /// <returns>The result.</returns>
    public static NifModelMorphTargetResult Typed(int morphIndex, int interpolatorBlock, IReadOnlyList<int> blocks,
        SceneCurve curve, NifModelCurve? keyCurve, bool hasCurve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return new NifModelMorphTargetResult(morphIndex, interpolatorBlock, blocks, curve, keyCurve, hasCurve, null,
            null);
    }

    /// <summary>A slot that stays native.</summary>
    /// <param name="morphIndex">The morph index (1 or more).</param>
    /// <param name="interpolatorBlock">The interpolator block, or -1.</param>
    /// <param name="blocks">The blocks the decision covers.</param>
    /// <param name="reason">The reason.</param>
    /// <param name="code">Its code.</param>
    /// <param name="hasCurve">Whether the slot stores keys or a spline handle (a blocked curve still counts).</param>
    /// <returns>The result.</returns>
    public static NifModelMorphTargetResult Native(int morphIndex, int interpolatorBlock, IReadOnlyList<int> blocks,
        string reason, string code, bool hasCurve = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new NifModelMorphTargetResult(morphIndex, interpolatorBlock, blocks, null, null, hasCurve, reason, code);
    }
}

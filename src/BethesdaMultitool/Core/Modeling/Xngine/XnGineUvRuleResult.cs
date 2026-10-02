namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The reference UV rule's answer for one plane (<see cref="XnGineUvRule" />): one derived value per stored corner,
///     in stored corner order, plus what the rule had to do to get there.
/// </summary>
/// <param name="Values">The derived u and v per corner, in 1/16 texel, in stored corner order.</param>
/// <param name="DegenerateFit">
///     True when the n-gon's plane fit through its first three corners is singular, so every corner takes its raw value
///     (the reference's fallback; corners 1 and 2 are then NOT accumulated).
/// </param>
/// <param name="UnfoldedValues">How many of corners 0 to 2's stored u and v values the packed-UV unfold changed (0 when it was not applied).</param>
internal sealed record XnGineUvRuleResult(IReadOnlyList<XnGineCornerUv> Values, bool DegenerateFit, int UnfoldedValues);

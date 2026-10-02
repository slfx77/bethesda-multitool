namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     What the 170-degree hop measured on one key pair (<see cref="NifAnimationRotationPathOracleTests" />), every angle in
///     degrees.
/// </summary>
/// <param name="PairDegrees">The rotation between the two typed keys (2 acos |dot|).</param>
/// <param name="Samples">The evaluator samples taken over the segment.</param>
/// <param name="EngineWorst">The worst angle between a sample and the engine's counter-warped nlerp (the oracle).</param>
/// <param name="SlerpWorst">The worst angle between a sample and a plain slerp of the keys (a control).</param>
/// <param name="NlerpWorst">The worst angle between a sample and a plain nlerp of the keys (a control).</param>
/// <param name="SlerpVersusNlerp">The largest angle between a plain slerp and a plain nlerp over the segment.</param>
internal readonly record struct NifAnimationPairOutcome(
    double PairDegrees,
    int Samples,
    double EngineWorst,
    double SlerpWorst,
    double NlerpWorst,
    double SlerpVersusNlerp);

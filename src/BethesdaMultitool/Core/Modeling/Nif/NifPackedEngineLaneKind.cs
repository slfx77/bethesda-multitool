namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     What the Xbox 360 engine's derived fourth skin lane does on one packed vertex (<see cref="NifPackedEngineLanes" />):
///     the derived weight r = 1 - ((w0 + w1) + w2) is zero, a new lane on a joint the vertex does not otherwise use,
///     merged into a stored lane on the same joint, or a negative lane on an otherwise unused joint. The kind names what
///     the vertex's lanes end up carrying, so a merge that cancels its lane is <see cref="Zero" /> and one that leaves
///     it negative is <see cref="Signed" />.
/// </summary>
internal enum NifPackedEngineLaneKind
{
    /// <summary>
    ///     The derived lane leaves no weight: r is exactly zero (the three stored weights already sum to one in Float32),
    ///     or r cancels the positive stored lane on its joint exactly, which then becomes a zero lane padded with joint 0.
    ///     Slot 3 is a zero lane.
    /// </summary>
    Zero,

    /// <summary>r is positive and its joint carries no positive stored lane: slot 3 is (that joint, r).</summary>
    NewLane,

    /// <summary>r is positive and its joint carries a positive stored lane: r is added to that lane.</summary>
    MergedPositive,

    /// <summary>r is negative and its joint carries a positive stored lane: r is added to that lane, which stays positive.</summary>
    MergedNegative,

    /// <summary>
    ///     The derived lane leaves a negative weight: r is negative on a joint that carries no positive stored lane (slot 3
    ///     is the signed lane, that joint and r), or r added to the positive stored lane on its joint takes it below zero
    ///     (that lane becomes the signed lane and slot 3 a zero lane).
    /// </summary>
    Signed
}

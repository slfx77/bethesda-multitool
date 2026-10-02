namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Why an NiTextKeyExtraData block's keys cannot become Shared events (<see cref="NifModelTextKeyEvents.Map" />). Every
///     reason is fail closed for the whole block: dropping or repairing one key would change the order or the count, which
///     plan section 1.5 forbids, so the block stays native state and no event is emitted.
/// </summary>
internal enum NifModelTextKeyBlock
{
    /// <summary>Not blocked: every key became an event.</summary>
    None = 0,

    /// <summary>The keys do not end exactly where the block ends, so the stored layout is not the one read.</summary>
    NotConsumedExactly,

    /// <summary>A key's Time is not finite; Shared's <c>SceneAnimationEvent</c> requires a finite time.</summary>
    NonFiniteTime,

    /// <summary>A key's label index lies outside the header string table.</summary>
    LabelOutOfRange
}

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One text key as stored, returned beside its Shared event for native state (plan section 1.5: "the raw bytes stay
///     native"). Index i of <see cref="NifModelTextKeyEventsResult.Keys" /> is the key behind event i.
/// </summary>
/// <param name="TimeBits">The key's Time bits as stored.</param>
/// <param name="LabelIndex">The stored string-table index; -1 for the NULL string and for an inline label.</param>
/// <param name="RawLabel">
///     The label bytes exactly as stored: the string-table entry, or the inline SizedString's bytes (views of the caller's
///     buffers, not copies); empty for the NULL string.
/// </param>
/// <param name="IsNullLabel">
///     True when an indexed label is the NULL string (index -1). Its event text is empty, like a stored empty string; this
///     flag keeps the difference.
/// </param>
internal readonly record struct NifModelTextKeyRecord(
    uint TimeBits,
    int LabelIndex,
    ReadOnlyMemory<byte> RawLabel,
    bool IsNullLabel);

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     One stored text key of an NiTextKeyExtraData block, exactly as stored: its Time bits and its Value, either a header
///     string-table index (20.1.0.1 and later) or the inline SizedString's raw bytes (earlier versions). Nothing is split,
///     trimmed, decoded or dropped. Part of <see cref="NifTextKeyExtraDataView" />.
/// </summary>
/// <param name="TimeBits">The raw bits of the key's Time.</param>
/// <param name="LabelIndex">The string-table index as stored (-1 for none); -1 when the label is inline.</param>
/// <param name="InlineLabel">The inline label's raw bytes (a slice of the caller's buffer); empty for an indexed label.</param>
internal readonly record struct NifTextKeyView(uint TimeBits, int LabelIndex, ReadOnlyMemory<byte> InlineLabel);

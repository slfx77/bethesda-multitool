namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless reading of one NiTextKeyExtraData block (read by <see cref="NifTextKeyReader.TryReadView" />): the extra
///     data's name as stored, and every text key in file order with its Time bits and its raw label. Unlike
///     <see cref="NifTextKeyReader.TryReadExact" />, nothing is decoded as ASCII, split on line breaks, trimmed, sorted or
///     rejected: an empty label, a CR LF pair, a padded label and a byte at or above 0x80 all survive.
/// </summary>
/// <param name="InlineStrings">True when the names are inline SizedStrings (before 20.1.0.1), false for string-table indices.</param>
/// <param name="NameIndex">The name's string-table index as stored (-1 for none, and when the name is inline or absent).</param>
/// <param name="InlineName">The inline name's raw bytes (empty when indexed, or absent in the legacy NetImmerse layout).</param>
/// <param name="LegacyNextExtraDataRef">The legacy NetImmerse (up to 4.2.2.0) Next Extra Data ref; null otherwise.</param>
/// <param name="LegacyRecordSize">The legacy NetImmerse (up to 4.2.2.0) Record Size; null otherwise.</param>
/// <param name="Keys">Every stored key, in file order.</param>
/// <param name="ConsumedExactly">True when the keys end exactly where the block ends.</param>
internal sealed record NifTextKeyExtraDataView(
    bool InlineStrings,
    int NameIndex,
    ReadOnlyMemory<byte> InlineName,
    int? LegacyNextExtraDataRef,
    uint? LegacyRecordSize,
    NifTextKeyView[] Keys,
    bool ConsumedExactly);

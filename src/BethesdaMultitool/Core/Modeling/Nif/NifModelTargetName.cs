namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One name a <see cref="NifModelTargetNames" /> map is built from: the stored bytes exactly as the file holds them
///     (a palette SizedString or a header string-table entry) and the block they name.
/// </summary>
/// <param name="RawName">The name bytes, exactly as stored.</param>
/// <param name="Block">The named block's index; -1 when a palette entry names no object.</param>
internal readonly record struct NifModelTargetName(ReadOnlyMemory<byte> RawName, int Block);

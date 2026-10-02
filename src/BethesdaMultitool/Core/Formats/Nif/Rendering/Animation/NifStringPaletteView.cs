namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless reading of one NiStringPalette block (read by
///     <see cref="NifControllerSequenceNameTrackReader.TryReadStringPaletteView" />; cut 2): the Palette SizedString's
///     bytes exactly as stored (NUL-delimited entries, which a 20.0.0.4 controlled block names by byte offset) and the
///     repeated Length word as stored. Nothing is decoded, split or trimmed; whether the Length word equals the payload's
///     byte count is the caller's check, as it is in the palette resolver.
/// </summary>
/// <param name="Palette">The Palette SizedString's payload bytes, exactly as stored.</param>
/// <param name="Length">The repeated Length word as stored (the exporter writes the palette's byte count).</param>
/// <param name="ConsumedExactly">True when the Length word ends exactly where the block ends.</param>
internal readonly record struct NifStringPaletteView(ReadOnlyMemory<byte> Palette, uint Length, bool ConsumedExactly);

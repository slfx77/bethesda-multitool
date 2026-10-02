namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     Where one decoded field sits in the file, for diagnostics. Spans are recorded for every field that is not
///     inside an array element; an array field gets one span covering all its elements.
/// </summary>
/// <param name="Path">
///     The field path from the block root, dot-separated (<c>Skin Transform.Rotation.m11</c>); a repeated field name
///     decoded more than once in one struct carries <c>#ordinal</c> (<c>Flags#1</c>).
/// </param>
/// <param name="Offset">The absolute file offset of the field's first byte.</param>
/// <param name="Length">The number of bytes the field occupies.</param>
internal sealed record NifFieldSpan(string Path, int Offset, int Length);

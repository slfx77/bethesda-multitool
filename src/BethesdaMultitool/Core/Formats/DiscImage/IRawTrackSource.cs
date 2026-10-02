namespace BethesdaMultitool.Core.Formats.DiscImage;

/// <summary>
///     A sector source that can also hand back the physical bytes of a sector — the 2352-byte
///     raw form for CD-DA extraction — whether those bytes come from a <c>.bin</c> on disk or out
///     of a CHD hunk.
/// </summary>
internal interface IRawTrackSource
{
    /// <summary>The physical sector bytes at <paramref name="lba" />; returns how many were written (the track's physical sector size).</summary>
    int ReadRawSector(long lba, Span<byte> buffer);
}

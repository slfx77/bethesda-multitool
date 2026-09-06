namespace BethesdaMultitool.Core.Formats.Travels.OblivionPsp;

/// <summary>
///     One record of an Oblivion PSP <c>GR.ARC</c> pack: a name from the pack's string table and
///     the extent of its uncompressed payload, already resolved to an absolute file offset (the
///     June 2006 revision stores the offset relative to the data area — see
///     <see cref="OblivionPspArchive" />).
/// </summary>
/// <param name="Index">Position in the record table, which is also the pack's own ordering.</param>
/// <param name="Name">The entry's name, Latin-1 from the pack's string table.</param>
/// <param name="NameOffset">Byte offset of the name inside the string table.</param>
/// <param name="Offset">Absolute byte offset of the payload in the file.</param>
/// <param name="Size">
///     Payload length. Zero is legal: the community-modified February 2007 disc truncates
///     <c>Hub_5_Demo</c> to nothing while keeping its record.
/// </param>
internal sealed record OblivionPspArchiveEntry(
    int Index,
    string Name,
    uint NameOffset,
    long Offset,
    long Size);

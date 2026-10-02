namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     One archive entry. Carries the resolved, format-neutral metadata every consumer needs
///     (path, size, offset, compressed state) plus the backing format record used to extract it.
///     The shared vocabulary of every <see cref="IArchiveBackend" />; it lived nested inside the
///     <see cref="Bsa.Index.ArchiveReader" /> facade until the M2.3 layering repair moved it
///     beside the contract, so the bottom tier no longer compiles against the format tier.
/// </summary>
public sealed record ArchiveEntry(
    string FullPath,
    string FolderPath,
    string Name,
    string Extension,
    long Size,
    long Offset,
    bool Compressed,
    object Record);

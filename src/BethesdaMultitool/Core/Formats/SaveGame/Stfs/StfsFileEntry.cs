namespace BethesdaMultitool.Core.Formats.SaveGame.Stfs;

/// <summary>
///     STFS file table entry for a file within the container.
/// </summary>
internal sealed record StfsFileEntry(
    string Filename,
    bool IsConsecutive,
    int ValidBlocks,
    int AllocatedBlocks,
    int StartBlock,
    int FileSize);

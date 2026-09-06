// Ported from NeversoftMultitool (MIT License), src/NeversoftMultitool/Core/Formats/DiscImage/
// CueTrack.cs, DiscTrackRegion.cs and DiscFileEntry.cs at commit 314bc9e0 (2026-08-14).
// See THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Formats.DiscImage;

/// <summary>One <c>TRACK</c> of a cue sheet, with the length of the file it lives in.</summary>
internal sealed record CueTrack(
    int Number,
    string Type,
    string FilePath,
    long FileLength,
    int SectorSize,
    long Index01Frames)
{
    public bool IsAudio => Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase);

    public long SectorCount => FileLength / SectorSize;
}

/// <summary>
///     One physical track region inside a raw image set: a byte range of a file mapped to an
///     absolute LBA range.
/// </summary>
internal sealed record DiscTrackRegion(
    long StartLba,
    long SectorCountValue,
    string FilePath,
    long FileByteOffset,
    int PhysicalSectorSize,
    bool IsAudio)
{
    public long EndLba => StartLba + SectorCountValue;
}

/// <summary>One file (or directory) of the disc's filesystem.</summary>
internal sealed record DiscFileEntry(
    string Directory,
    string Name,
    long ExtentLba,
    long Size,
    bool IsDirectory)
{
    public string FullPath => Directory.Length == 0 ? Name : $"{Directory}/{Name}";
}

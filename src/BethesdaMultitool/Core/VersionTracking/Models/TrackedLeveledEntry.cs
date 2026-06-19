namespace BethesdaMultitool.Core.VersionTracking.Models;

/// <summary>
///     Single entry in a leveled list for version tracking.
/// </summary>
public record TrackedLeveledEntry(ushort Level, uint FormId, ushort Count);

namespace BethesdaMultitool.Core.VersionTracking.Models;

/// <summary>A single quest stage (index + log entry + flags) for version tracking.</summary>
public record TrackedQuestStage(int Index, string? LogEntry, byte Flags);

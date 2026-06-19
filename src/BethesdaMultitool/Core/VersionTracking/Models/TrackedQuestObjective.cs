namespace BethesdaMultitool.Core.VersionTracking.Models;

/// <summary>A single quest objective (index + display text) for version tracking.</summary>
public record TrackedQuestObjective(int Index, string? DisplayText);

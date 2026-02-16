namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight quest snapshot for version tracking.
/// </summary>
public record TrackedQuest
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public byte Flags { get; init; }
    public byte Priority { get; init; }
    public float QuestDelay { get; init; }
    public uint? ScriptFormId { get; init; }
    public List<TrackedQuestStage> Stages { get; init; } = [];
    public List<TrackedQuestObjective> Objectives { get; init; } = [];
}

public record TrackedQuestStage(int Index, string? LogEntry, byte Flags);

public record TrackedQuestObjective(int Index, string? DisplayText);

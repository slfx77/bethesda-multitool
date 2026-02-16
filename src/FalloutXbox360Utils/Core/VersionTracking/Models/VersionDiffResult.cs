namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Complete diff result between two build snapshots.
/// </summary>
public record VersionDiffResult
{
    public required BuildInfo FromBuild { get; init; }
    public required BuildInfo ToBuild { get; init; }

    public List<RecordChange> QuestChanges { get; init; } = [];
    public List<RecordChange> NpcChanges { get; init; } = [];
    public List<RecordChange> DialogueChanges { get; init; } = [];
    public List<RecordChange> WeaponChanges { get; init; } = [];
    public List<RecordChange> ArmorChanges { get; init; } = [];
    public List<RecordChange> ItemChanges { get; init; } = [];
    public List<RecordChange> ScriptChanges { get; init; } = [];
    public List<RecordChange> LocationChanges { get; init; } = [];
    public List<RecordChange> PlacementChanges { get; init; } = [];
    public List<RecordChange> CreatureChanges { get; init; } = [];
    public List<RecordChange> PerkChanges { get; init; } = [];
    public List<RecordChange> AmmoChanges { get; init; } = [];
    public List<RecordChange> LeveledListChanges { get; init; } = [];
    public List<RecordChange> NoteChanges { get; init; } = [];
    public List<RecordChange> TerminalChanges { get; init; } = [];

    /// <summary>All changes across all categories.</summary>
    public IEnumerable<RecordChange> AllChanges =>
        QuestChanges.Concat(NpcChanges).Concat(DialogueChanges)
            .Concat(WeaponChanges).Concat(ArmorChanges).Concat(ItemChanges)
            .Concat(ScriptChanges).Concat(LocationChanges).Concat(PlacementChanges)
            .Concat(CreatureChanges).Concat(PerkChanges).Concat(AmmoChanges)
            .Concat(LeveledListChanges).Concat(NoteChanges).Concat(TerminalChanges);

    public int TotalAdded => AllChanges.Count(c => c.ChangeType == ChangeType.Added);
    public int TotalRemoved => AllChanges.Count(c => c.ChangeType == ChangeType.Removed);
    public int TotalChanged => AllChanges.Count(c => c.ChangeType == ChangeType.Changed);
}

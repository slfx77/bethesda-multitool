namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Complete snapshot of all tracked records from a single build source.
///     Dictionaries are keyed by FormID for O(1) lookup during diffing.
/// </summary>
public record VersionSnapshot
{
    /// <summary>Metadata about the build this snapshot was extracted from.</summary>
    public required BuildInfo Build { get; init; }

    public Dictionary<uint, TrackedQuest> Quests { get; init; } = [];
    public Dictionary<uint, TrackedNpc> Npcs { get; init; } = [];
    public Dictionary<uint, TrackedDialogue> Dialogues { get; init; } = [];
    public Dictionary<uint, TrackedWeapon> Weapons { get; init; } = [];
    public Dictionary<uint, TrackedArmor> Armor { get; init; } = [];
    public Dictionary<uint, TrackedItem> Items { get; init; } = [];
    public Dictionary<uint, TrackedScript> Scripts { get; init; } = [];
    public Dictionary<uint, TrackedLocation> Locations { get; init; } = [];
    public Dictionary<uint, TrackedPlacement> Placements { get; init; } = [];
    public Dictionary<uint, TrackedCreature> Creatures { get; init; } = [];
    public Dictionary<uint, TrackedPerk> Perks { get; init; } = [];
    public Dictionary<uint, TrackedAmmo> Ammo { get; init; } = [];
    public Dictionary<uint, TrackedLeveledList> LeveledLists { get; init; } = [];
    public Dictionary<uint, TrackedNote> Notes { get; init; } = [];
    public Dictionary<uint, TrackedTerminal> Terminals { get; init; } = [];

    /// <summary>Total number of records across all categories.</summary>
    public int TotalRecordCount =>
        Quests.Count + Npcs.Count + Dialogues.Count + Weapons.Count +
        Armor.Count + Items.Count + Scripts.Count + Locations.Count + Placements.Count +
        Creatures.Count + Perks.Count + Ammo.Count + LeveledLists.Count + Notes.Count + Terminals.Count;

    /// <summary>When the snapshot was extracted.</summary>
    public DateTimeOffset ExtractedAt { get; init; } = DateTimeOffset.UtcNow;
}

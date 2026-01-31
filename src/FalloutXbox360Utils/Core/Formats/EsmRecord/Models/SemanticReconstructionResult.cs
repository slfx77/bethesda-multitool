namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Aggregated semantic reconstruction result from a memory dump.
/// </summary>
public record SemanticReconstructionResult
{
    // Characters
    /// <summary>Reconstructed NPC records.</summary>
    public List<ReconstructedNpc> Npcs { get; init; } = [];

    /// <summary>Reconstructed Creature records.</summary>
    public List<ReconstructedCreature> Creatures { get; init; } = [];

    /// <summary>Reconstructed Race records.</summary>
    public List<ReconstructedRace> Races { get; init; } = [];

    /// <summary>Reconstructed Faction records.</summary>
    public List<ReconstructedFaction> Factions { get; init; } = [];

    // Quests and Dialogue
    /// <summary>Reconstructed Quest records.</summary>
    public List<ReconstructedQuest> Quests { get; init; } = [];

    /// <summary>Reconstructed Dialog Topic records.</summary>
    public List<ReconstructedDialogTopic> DialogTopics { get; init; } = [];

    /// <summary>Reconstructed Dialogue (INFO) records.</summary>
    public List<ReconstructedDialogue> Dialogues { get; init; } = [];

    /// <summary>Reconstructed Note records.</summary>
    public List<ReconstructedNote> Notes { get; init; } = [];

    /// <summary>Reconstructed Book records.</summary>
    public List<ReconstructedBook> Books { get; init; } = [];

    /// <summary>Reconstructed Terminal records.</summary>
    public List<ReconstructedTerminal> Terminals { get; init; } = [];

    // Items
    /// <summary>Reconstructed Weapon records.</summary>
    public List<ReconstructedWeapon> Weapons { get; init; } = [];

    /// <summary>Reconstructed Armor records.</summary>
    public List<ReconstructedArmor> Armor { get; init; } = [];

    /// <summary>Reconstructed Ammo records.</summary>
    public List<ReconstructedAmmo> Ammo { get; init; } = [];

    /// <summary>Reconstructed Consumable (ALCH) records.</summary>
    public List<ReconstructedConsumable> Consumables { get; init; } = [];

    /// <summary>Reconstructed Misc Item records.</summary>
    public List<ReconstructedMiscItem> MiscItems { get; init; } = [];

    /// <summary>Reconstructed Key records.</summary>
    public List<ReconstructedKey> Keys { get; init; } = [];

    /// <summary>Reconstructed Container records.</summary>
    public List<ReconstructedContainer> Containers { get; init; } = [];

    // Abilities
    /// <summary>Reconstructed Perk records.</summary>
    public List<ReconstructedPerk> Perks { get; init; } = [];

    /// <summary>Reconstructed Spell records.</summary>
    public List<ReconstructedSpell> Spells { get; init; } = [];

    // World
    /// <summary>Reconstructed Cell records.</summary>
    public List<ReconstructedCell> Cells { get; init; } = [];

    /// <summary>Reconstructed Worldspace records.</summary>
    public List<ReconstructedWorldspace> Worldspaces { get; init; } = [];

    /// <summary>Map markers extracted from REFR records with XMRK subrecord.</summary>
    public List<PlacedReference> MapMarkers { get; init; } = [];

    /// <summary>Reconstructed Leveled List records (LVLI/LVLN/LVLC).</summary>
    public List<ReconstructedLeveledList> LeveledLists { get; init; } = [];

    // Game Data
    /// <summary>Reconstructed Game Setting (GMST) records.</summary>
    public List<ReconstructedGameSetting> GameSettings { get; init; } = [];

    /// <summary>FormID to Editor ID mapping built during reconstruction.</summary>
    public Dictionary<uint, string> FormIdToEditorId { get; init; } = [];

    /// <summary>FormID to display name (FullName) mapping built from runtime hash table entries.</summary>
    public Dictionary<uint, string> FormIdToDisplayName { get; init; } = [];

    /// <summary>Total records processed.</summary>
    public int TotalRecordsProcessed { get; init; }

    /// <summary>Number of records successfully reconstructed.</summary>
    public int TotalRecordsReconstructed =>
        Npcs.Count + Creatures.Count + Races.Count + Factions.Count +
        Quests.Count + DialogTopics.Count + Dialogues.Count + Notes.Count + Books.Count + Terminals.Count +
        Weapons.Count + Armor.Count + Ammo.Count + Consumables.Count + MiscItems.Count + Keys.Count + Containers.Count +
        Perks.Count + Spells.Count + Cells.Count + Worldspaces.Count + MapMarkers.Count + LeveledLists.Count +
        GameSettings.Count;

    /// <summary>
    ///     Counts of record types that were detected but not fully reconstructed.
    ///     Used for the "Other Records" summary section in split reports.
    /// </summary>
    public Dictionary<string, int> UnreconstructedTypeCounts { get; init; } = [];
}

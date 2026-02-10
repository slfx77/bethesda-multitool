namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Aggregated semantic reconstruction result from a memory dump.
/// </summary>
public record RecordCollection
{
    // Characters
    /// <summary>Reconstructed NPC records.</summary>
    public List<NpcRecord> Npcs { get; init; } = [];

    /// <summary>Reconstructed Creature records.</summary>
    public List<CreatureRecord> Creatures { get; init; } = [];

    /// <summary>Reconstructed Race records.</summary>
    public List<RaceRecord> Races { get; init; } = [];

    /// <summary>Reconstructed Faction records.</summary>
    public List<FactionRecord> Factions { get; init; } = [];

    // Quests and Dialogue
    /// <summary>Reconstructed Quest records.</summary>
    public List<QuestRecord> Quests { get; init; } = [];

    /// <summary>Reconstructed Dialog Topic records.</summary>
    public List<DialogTopicRecord> DialogTopics { get; init; } = [];

    /// <summary>Reconstructed Dialogue (INFO) records.</summary>
    public List<DialogueRecord> Dialogues { get; init; } = [];

    /// <summary>Hierarchical dialogue tree: Quest → Topic → INFO chains with cross-topic links.</summary>
    public DialogueTreeResult? DialogueTree { get; init; }

    /// <summary>Reconstructed Note records.</summary>
    public List<NoteRecord> Notes { get; init; } = [];

    /// <summary>Reconstructed Book records.</summary>
    public List<BookRecord> Books { get; init; } = [];

    /// <summary>Reconstructed Terminal records.</summary>
    public List<TerminalRecord> Terminals { get; init; } = [];

    /// <summary>Reconstructed Script (SCPT) records.</summary>
    public List<ScriptRecord> Scripts { get; init; } = [];

    // Items
    /// <summary>Reconstructed Weapon records.</summary>
    public List<WeaponRecord> Weapons { get; init; } = [];

    /// <summary>Reconstructed Armor records.</summary>
    public List<ArmorRecord> Armor { get; init; } = [];

    /// <summary>Reconstructed Ammo records.</summary>
    public List<AmmoRecord> Ammo { get; init; } = [];

    /// <summary>Reconstructed Consumable (ALCH) records.</summary>
    public List<ConsumableRecord> Consumables { get; init; } = [];

    /// <summary>Reconstructed Misc Item records.</summary>
    public List<MiscItemRecord> MiscItems { get; init; } = [];

    /// <summary>Reconstructed Key records.</summary>
    public List<KeyRecord> Keys { get; init; } = [];

    /// <summary>Reconstructed Container records.</summary>
    public List<ContainerRecord> Containers { get; init; } = [];

    // Abilities
    /// <summary>Reconstructed Perk records.</summary>
    public List<PerkRecord> Perks { get; init; } = [];

    /// <summary>Reconstructed Spell records.</summary>
    public List<SpellRecord> Spells { get; init; } = [];

    // World
    /// <summary>Reconstructed Cell records.</summary>
    public List<CellRecord> Cells { get; init; } = [];

    /// <summary>Reconstructed Worldspace records.</summary>
    public List<WorldspaceRecord> Worldspaces { get; init; } = [];

    /// <summary>Map markers extracted from REFR records with XMRK subrecord.</summary>
    public List<PlacedReference> MapMarkers { get; init; } = [];

    /// <summary>Reconstructed Leveled List records (LVLI/LVLN/LVLC).</summary>
    public List<LeveledListRecord> LeveledLists { get; init; } = [];

    // Game Data
    /// <summary>Reconstructed Game Setting (GMST) records.</summary>
    public List<GameSettingRecord> GameSettings { get; init; } = [];

    /// <summary>Reconstructed Global Variable (GLOB) records.</summary>
    public List<GlobalRecord> Globals { get; init; } = [];

    /// <summary>Reconstructed Enchantment (ENCH) records.</summary>
    public List<EnchantmentRecord> Enchantments { get; init; } = [];

    /// <summary>Reconstructed Base Effect (MGEF) records.</summary>
    public List<BaseEffectRecord> BaseEffects { get; init; } = [];

    /// <summary>Reconstructed Weapon Mod (IMOD) records.</summary>
    public List<WeaponModRecord> WeaponMods { get; init; } = [];

    /// <summary>Reconstructed Recipe (RCPE) records.</summary>
    public List<RecipeRecord> Recipes { get; init; } = [];

    /// <summary>Reconstructed Challenge (CHAL) records.</summary>
    public List<ChallengeRecord> Challenges { get; init; } = [];

    /// <summary>Reconstructed Reputation (REPU) records.</summary>
    public List<ReputationRecord> Reputations { get; init; } = [];

    /// <summary>Reconstructed Projectile (PROJ) records.</summary>
    public List<ProjectileRecord> Projectiles { get; init; } = [];

    /// <summary>Reconstructed Explosion (EXPL) records.</summary>
    public List<ExplosionRecord> Explosions { get; init; } = [];

    /// <summary>Reconstructed Message (MESG) records.</summary>
    public List<MessageRecord> Messages { get; init; } = [];

    /// <summary>Reconstructed Class (CLAS) records.</summary>
    public List<ClassRecord> Classes { get; init; } = [];

    /// <summary>FormID to Editor ID mapping built during reconstruction.</summary>
    public Dictionary<uint, string> FormIdToEditorId { get; init; } = [];

    /// <summary>FormID to display name (FullName) mapping built from runtime hash table entries.</summary>
    public Dictionary<uint, string> FormIdToDisplayName { get; init; } = [];

    /// <summary>Total records processed.</summary>
    public int TotalRecordsProcessed { get; init; }

    /// <summary>Number of records successfully reconstructed.</summary>
    public int TotalRecordsReconstructed =>
        Npcs.Count + Creatures.Count + Races.Count + Factions.Count +
        Quests.Count + DialogTopics.Count + Dialogues.Count + Notes.Count + Books.Count + Terminals.Count + Scripts.Count +
        Weapons.Count + Armor.Count + Ammo.Count + Consumables.Count + MiscItems.Count + Keys.Count + Containers.Count +
        Perks.Count + Spells.Count + Cells.Count + Worldspaces.Count + MapMarkers.Count + LeveledLists.Count +
        GameSettings.Count + Globals.Count + Enchantments.Count + BaseEffects.Count +
        WeaponMods.Count + Recipes.Count + Challenges.Count + Reputations.Count +
        Projectiles.Count + Explosions.Count + Messages.Count + Classes.Count;

    /// <summary>
    ///     Counts of record types that were detected but not fully reconstructed.
    ///     Used for the "Other Records" summary section in split reports.
    /// </summary>
    public Dictionary<string, int> UnreconstructedTypeCounts { get; init; } = [];
}

using FalloutXbox360Utils.Core.Formats.Esm.Export;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>A single world placement of a base object in a cell.</summary>
public record WorldPlacement(PlacedReference Ref, CellRecord Cell);

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

    /// <summary>Reconstructed Form ID List (FLST) records.</summary>
    public List<FormListRecord> FormLists { get; init; } = [];

    /// <summary>Reconstructed Activator (ACTI) records.</summary>
    public List<ActivatorRecord> Activators { get; init; } = [];

    /// <summary>Reconstructed Light (LIGH) records.</summary>
    public List<LightRecord> Lights { get; init; } = [];

    /// <summary>Reconstructed Door (DOOR) records.</summary>
    public List<DoorRecord> Doors { get; init; } = [];

    /// <summary>Reconstructed Static (STAT) records.</summary>
    public List<StaticRecord> Statics { get; init; } = [];

    /// <summary>Reconstructed Furniture (FURN) records.</summary>
    public List<FurnitureRecord> Furniture { get; init; } = [];

    // AI
    /// <summary>Reconstructed AI Package (PACK) records.</summary>
    public List<PackageRecord> Packages { get; init; } = [];

    // Generic
    /// <summary>Generic ESM records for types without specialized models (MSTT, TACT, CAMS, ANIO, etc.).</summary>
    public List<GenericEsmRecord> GenericRecords { get; init; } = [];

    // Specialized Phase 2 records
    /// <summary>Reconstructed Sound (SOUN) records.</summary>
    public List<SoundRecord> Sounds { get; init; } = [];

    /// <summary>Reconstructed Texture Set (TXST) records.</summary>
    public List<TextureSetRecord> TextureSets { get; init; } = [];

    /// <summary>Reconstructed Armor Addon (ARMA) records.</summary>
    public List<ArmaRecord> ArmorAddons { get; init; } = [];

    /// <summary>Reconstructed Water (WATR) records.</summary>
    public List<WaterRecord> Water { get; init; } = [];

    /// <summary>Reconstructed Body Part Data (BPTD) records.</summary>
    public List<BodyPartDataRecord> BodyPartData { get; init; } = [];

    /// <summary>Reconstructed Actor Value Info (AVIF) records.</summary>
    public List<ActorValueInfoRecord> ActorValueInfos { get; init; } = [];

    /// <summary>Reconstructed Combat Style (CSTY) records.</summary>
    public List<CombatStyleRecord> CombatStyles { get; init; } = [];

    /// <summary>Reconstructed Lighting Template (LGTM) records.</summary>
    public List<LightingTemplateRecord> LightingTemplates { get; init; } = [];

    /// <summary>Reconstructed Navigation Mesh (NAVM) records.</summary>
    public List<NavMeshRecord> NavMeshes { get; init; } = [];

    /// <summary>Reconstructed Weather (WTHR) records.</summary>
    public List<WeatherRecord> Weather { get; init; } = [];

    /// <summary>
    ///     FormID → model path (.nif) mapping from STAT, ACTI, DOOR, LIGH, FURN, WEAP, ARMO, AMMO, ALCH, MISC, BOOK, CONT
    ///     records.
    /// </summary>
    public Dictionary<uint, string> ModelPathIndex { get; init; } = [];

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
        Scripts.Count +
        Weapons.Count + Armor.Count + Ammo.Count + Consumables.Count + MiscItems.Count + Keys.Count + Containers.Count +
        Perks.Count + Spells.Count + Cells.Count + Worldspaces.Count + MapMarkers.Count + LeveledLists.Count +
        GameSettings.Count + Globals.Count + Enchantments.Count + BaseEffects.Count +
        WeaponMods.Count + Recipes.Count + Challenges.Count + Reputations.Count +
        Projectiles.Count + Explosions.Count + Messages.Count + Classes.Count +
        FormLists.Count + Activators.Count +
        Lights.Count + Doors.Count + Statics.Count + Furniture.Count +
        Packages.Count +
        GenericRecords.Count +
        Sounds.Count + TextureSets.Count + ArmorAddons.Count + Water.Count +
        BodyPartData.Count + ActorValueInfos.Count + CombatStyles.Count +
        LightingTemplates.Count + NavMeshes.Count + Weather.Count;

    /// <summary>
    ///     Counts of record types that were detected but not fully reconstructed.
    ///     Used for the "Other Records" summary section in split reports.
    /// </summary>
    public Dictionary<string, int> UnreconstructedTypeCounts { get; init; } = [];

    /// <summary>
    ///     Creates a new RecordCollection by merging this collection with records from
    ///     another collection. For duplicate FormIDs, records from <paramref name="overlay"/>
    ///     (the later load order entry) take precedence.
    /// </summary>
    public RecordCollection MergeWith(RecordCollection overlay)
    {
        return new RecordCollection
        {
            // Characters
            Npcs = MergeList(Npcs, overlay.Npcs, r => r.FormId),
            Creatures = MergeList(Creatures, overlay.Creatures, r => r.FormId),
            Races = MergeList(Races, overlay.Races, r => r.FormId),
            Factions = MergeList(Factions, overlay.Factions, r => r.FormId),

            // Quests and Dialogue
            Quests = MergeList(Quests, overlay.Quests, r => r.FormId),
            DialogTopics = MergeList(DialogTopics, overlay.DialogTopics, r => r.FormId),
            Dialogues = MergeList(Dialogues, overlay.Dialogues, r => r.FormId),
            DialogueTree = overlay.DialogueTree ?? DialogueTree,
            Notes = MergeList(Notes, overlay.Notes, r => r.FormId),
            Books = MergeList(Books, overlay.Books, r => r.FormId),
            Terminals = MergeList(Terminals, overlay.Terminals, r => r.FormId),
            Scripts = MergeList(Scripts, overlay.Scripts, r => r.FormId),

            // Items
            Weapons = MergeList(Weapons, overlay.Weapons, r => r.FormId),
            Armor = MergeList(Armor, overlay.Armor, r => r.FormId),
            Ammo = MergeList(Ammo, overlay.Ammo, r => r.FormId),
            Consumables = MergeList(Consumables, overlay.Consumables, r => r.FormId),
            MiscItems = MergeList(MiscItems, overlay.MiscItems, r => r.FormId),
            Keys = MergeList(Keys, overlay.Keys, r => r.FormId),
            Containers = MergeList(Containers, overlay.Containers, r => r.FormId),

            // Abilities
            Perks = MergeList(Perks, overlay.Perks, r => r.FormId),
            Spells = MergeList(Spells, overlay.Spells, r => r.FormId),

            // World
            Cells = MergeList(Cells, overlay.Cells, r => r.FormId),
            Worldspaces = MergeList(Worldspaces, overlay.Worldspaces, r => r.FormId),
            MapMarkers = MergeList(MapMarkers, overlay.MapMarkers, r => r.FormId),
            LeveledLists = MergeList(LeveledLists, overlay.LeveledLists, r => r.FormId),

            // Game Data
            GameSettings = MergeList(GameSettings, overlay.GameSettings, r => r.FormId),
            Globals = MergeList(Globals, overlay.Globals, r => r.FormId),
            Enchantments = MergeList(Enchantments, overlay.Enchantments, r => r.FormId),
            BaseEffects = MergeList(BaseEffects, overlay.BaseEffects, r => r.FormId),
            WeaponMods = MergeList(WeaponMods, overlay.WeaponMods, r => r.FormId),
            Recipes = MergeList(Recipes, overlay.Recipes, r => r.FormId),
            Challenges = MergeList(Challenges, overlay.Challenges, r => r.FormId),
            Reputations = MergeList(Reputations, overlay.Reputations, r => r.FormId),
            Projectiles = MergeList(Projectiles, overlay.Projectiles, r => r.FormId),
            Explosions = MergeList(Explosions, overlay.Explosions, r => r.FormId),
            Messages = MergeList(Messages, overlay.Messages, r => r.FormId),
            Classes = MergeList(Classes, overlay.Classes, r => r.FormId),
            FormLists = MergeList(FormLists, overlay.FormLists, r => r.FormId),
            Activators = MergeList(Activators, overlay.Activators, r => r.FormId),
            Lights = MergeList(Lights, overlay.Lights, r => r.FormId),
            Doors = MergeList(Doors, overlay.Doors, r => r.FormId),
            Statics = MergeList(Statics, overlay.Statics, r => r.FormId),
            Furniture = MergeList(Furniture, overlay.Furniture, r => r.FormId),

            // AI
            Packages = MergeList(Packages, overlay.Packages, r => r.FormId),

            // Generic
            GenericRecords = MergeList(GenericRecords, overlay.GenericRecords, r => r.FormId),

            // Specialized
            Sounds = MergeList(Sounds, overlay.Sounds, r => r.FormId),
            TextureSets = MergeList(TextureSets, overlay.TextureSets, r => r.FormId),
            ArmorAddons = MergeList(ArmorAddons, overlay.ArmorAddons, r => r.FormId),
            Water = MergeList(Water, overlay.Water, r => r.FormId),
            BodyPartData = MergeList(BodyPartData, overlay.BodyPartData, r => r.FormId),
            ActorValueInfos = MergeList(ActorValueInfos, overlay.ActorValueInfos, r => r.FormId),
            CombatStyles = MergeList(CombatStyles, overlay.CombatStyles, r => r.FormId),
            LightingTemplates = MergeList(LightingTemplates, overlay.LightingTemplates, r => r.FormId),
            NavMeshes = MergeList(NavMeshes, overlay.NavMeshes, r => r.FormId),
            Weather = MergeList(Weather, overlay.Weather, r => r.FormId),

            // Dictionaries: overlay overwrites base
            ModelPathIndex = MergeDictionary(ModelPathIndex, overlay.ModelPathIndex),
            FormIdToEditorId = MergeDictionary(FormIdToEditorId, overlay.FormIdToEditorId),
            FormIdToDisplayName = MergeDictionary(FormIdToDisplayName, overlay.FormIdToDisplayName),
            UnreconstructedTypeCounts = MergeDictionary(UnreconstructedTypeCounts, overlay.UnreconstructedTypeCounts),

            TotalRecordsProcessed = TotalRecordsProcessed + overlay.TotalRecordsProcessed
        };
    }

    /// <summary>Creates a FormIdResolver from this collection's dictionaries.</summary>
    public FormIdResolver CreateResolver(Dictionary<uint, string>? overrideEditorIds = null)
    {
        return new FormIdResolver(
            overrideEditorIds ?? FormIdToEditorId,
            FormIdToDisplayName,
            BuildRefToBaseMap());
    }

    /// <summary>
    ///     Builds a reverse index: base object FormID → list of world placements.
    ///     Used for "Use Info" in the data browser (GECK-style placement count).
    /// </summary>
    public Dictionary<uint, List<WorldPlacement>> BuildBaseToPlacementsMap()
    {
        var map = new Dictionary<uint, List<WorldPlacement>>();
        foreach (var cell in Cells)
        {
            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.BaseFormId == 0)
                {
                    continue;
                }

                if (!map.TryGetValue(obj.BaseFormId, out var list))
                {
                    list = [];
                    map[obj.BaseFormId] = list;
                }

                list.Add(new WorldPlacement(obj, cell));
            }
        }

        return map;
    }

    private Dictionary<uint, uint> BuildRefToBaseMap()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var cell in Cells)
        {
            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.FormId != 0 && obj.BaseFormId != 0)
                {
                    map.TryAdd(obj.FormId, obj.BaseFormId);
                }
            }
        }

        foreach (var marker in MapMarkers)
        {
            if (marker.FormId != 0 && marker.BaseFormId != 0)
            {
                map.TryAdd(marker.FormId, marker.BaseFormId);
            }
        }

        return map;
    }

    /// <summary>
    ///     Merges two lists, deduplicating by FormID. Items from <paramref name="overlay"/>
    ///     take precedence over items from <paramref name="baseList"/> for the same FormID.
    /// </summary>
    private static List<T> MergeList<T>(List<T> baseList, List<T> overlay, Func<T, uint> formIdSelector)
    {
        if (baseList.Count == 0) return new List<T>(overlay);
        if (overlay.Count == 0) return new List<T>(baseList);

        var overlayIds = new HashSet<uint>(overlay.Select(formIdSelector));
        var merged = new List<T>(baseList.Count + overlay.Count);

        foreach (var item in baseList)
        {
            if (!overlayIds.Contains(formIdSelector(item)))
            {
                merged.Add(item);
            }
        }

        merged.AddRange(overlay);
        return merged;
    }

    private static Dictionary<TKey, TValue> MergeDictionary<TKey, TValue>(
        Dictionary<TKey, TValue> baseDict, Dictionary<TKey, TValue> overlay) where TKey : notnull
    {
        var merged = new Dictionary<TKey, TValue>(baseDict);
        foreach (var (k, v) in overlay)
        {
            merged[k] = v;
        }

        return merged;
    }
}

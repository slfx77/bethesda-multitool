using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Aggregated semantic parsing result from a memory dump.
/// </summary>
public record RecordCollection
{
    // Characters
    /// <summary>Parsed NPC records.</summary>
    public List<NpcRecord> Npcs { get; init; } = [];

    /// <summary>Parsed Creature records.</summary>
    public List<CreatureRecord> Creatures { get; init; } = [];

    /// <summary>Parsed Race records.</summary>
    public List<RaceRecord> Races { get; init; } = [];

    /// <summary>Parsed Faction records.</summary>
    public List<FactionRecord> Factions { get; init; } = [];

    /// <summary>Parsed Encounter Zone (ECZN) records.</summary>
    public List<EncounterZoneRecord> EncounterZones { get; init; } = [];

    // Quests and Dialogue
    /// <summary>Parsed Quest records.</summary>
    public List<QuestRecord> Quests { get; init; } = [];

    /// <summary>Parsed Dialog Topic records.</summary>
    public List<DialogTopicRecord> DialogTopics { get; init; } = [];

    /// <summary>Parsed Dialogue (INFO) records.</summary>
    public List<DialogueRecord> Dialogues { get; init; } = [];

    /// <summary>Hierarchical dialogue tree: Quest → Topic → INFO chains with cross-topic links.</summary>
    public DialogueTreeResult? DialogueTree { get; init; }

    /// <summary>Parsed Note records.</summary>
    public List<NoteRecord> Notes { get; init; } = [];

    /// <summary>Parsed Book records.</summary>
    public List<BookRecord> Books { get; init; } = [];

    /// <summary>Parsed Terminal records.</summary>
    public List<TerminalRecord> Terminals { get; init; } = [];

    /// <summary>Parsed Script (SCPT) records.</summary>
    public List<ScriptRecord> Scripts { get; init; } = [];

    // Items
    /// <summary>Parsed Weapon records.</summary>
    public List<WeaponRecord> Weapons { get; init; } = [];

    /// <summary>Parsed Armor records.</summary>
    public List<ArmorRecord> Armor { get; init; } = [];

    /// <summary>Parsed Ammo records.</summary>
    public List<AmmoRecord> Ammo { get; init; } = [];

    /// <summary>Parsed Consumable (ALCH) records.</summary>
    public List<ConsumableRecord> Consumables { get; init; } = [];

    /// <summary>Parsed Misc Item records.</summary>
    public List<MiscItemRecord> MiscItems { get; init; } = [];

    /// <summary>Parsed Key records.</summary>
    public List<KeyRecord> Keys { get; init; } = [];

    /// <summary>Parsed Container records.</summary>
    public List<ContainerRecord> Containers { get; init; } = [];

    // Abilities
    /// <summary>Parsed Perk records.</summary>
    public List<PerkRecord> Perks { get; init; } = [];

    /// <summary>Parsed Spell records.</summary>
    public List<SpellRecord> Spells { get; init; } = [];

    // World
    /// <summary>Parsed Cell records.</summary>
    public List<CellRecord> Cells { get; init; } = [];

    /// <summary>Parsed Worldspace records.</summary>
    public List<WorldspaceRecord> Worldspaces { get; init; } = [];

    /// <summary>Map markers extracted from REFR records with XMRK subrecord.</summary>
    public List<PlacedReference> MapMarkers { get; init; } = [];

    /// <summary>Parsed Leveled List records (LVLI/LVLN/LVLC).</summary>
    public List<LeveledListRecord> LeveledLists { get; init; } = [];

    // Game Data
    /// <summary>Parsed Game Setting (GMST) records.</summary>
    public List<GameSettingRecord> GameSettings { get; init; } = [];

    /// <summary>Parsed Global Variable (GLOB) records.</summary>
    public List<GlobalRecord> Globals { get; init; } = [];

    /// <summary>Parsed Enchantment (ENCH) records.</summary>
    public List<EnchantmentRecord> Enchantments { get; init; } = [];

    /// <summary>Parsed Base Effect (MGEF) records.</summary>
    public List<BaseEffectRecord> BaseEffects { get; init; } = [];

    /// <summary>Parsed Weapon Mod (IMOD) records.</summary>
    public List<WeaponModRecord> WeaponMods { get; init; } = [];

    /// <summary>Parsed Recipe (RCPE) records.</summary>
    public List<RecipeRecord> Recipes { get; init; } = [];

    /// <summary>Parsed Recipe Category (RCCT) records.</summary>
    public List<RecipeCategoryRecord> RecipeCategories { get; init; } = [];

    /// <summary>Parsed Constructible Object (COBJ) records.</summary>
    public List<ConstructibleObjectRecord> ConstructibleObjects { get; init; } = [];

    /// <summary>Parsed Challenge (CHAL) records.</summary>
    public List<ChallengeRecord> Challenges { get; init; } = [];

    /// <summary>Parsed Reputation (REPU) records.</summary>
    public List<ReputationRecord> Reputations { get; init; } = [];

    /// <summary>Parsed Projectile (PROJ) records.</summary>
    public List<ProjectileRecord> Projectiles { get; init; } = [];

    /// <summary>Parsed Explosion (EXPL) records.</summary>
    public List<ExplosionRecord> Explosions { get; init; } = [];

    /// <summary>Parsed Message (MESG) records.</summary>
    public List<MessageRecord> Messages { get; init; } = [];

    /// <summary>Parsed Class (CLAS) records.</summary>
    public List<ClassRecord> Classes { get; init; } = [];

    /// <summary>Parsed Eyes (EYES) records.</summary>
    public List<EyesRecord> Eyes { get; init; } = [];

    /// <summary>Parsed Hair (HAIR) records.</summary>
    public List<HairRecord> Hair { get; init; } = [];

    /// <summary>Parsed Head Part (HDPT) records.</summary>
    public List<HeadPartRecord> HeadParts { get; init; } = [];

    /// <summary>Parsed Voice Type (VTYP) records.</summary>
    public List<VoiceTypeRecord> VoiceTypes { get; init; } = [];

    /// <summary>Parsed Menu Icon (MICN) records.</summary>
    public List<MenuIconRecord> MenuIcons { get; init; } = [];

    /// <summary>Parsed Load Screen Type (LSCT) records.</summary>
    public List<LoadScreenTypeRecord> LoadScreenTypes { get; init; } = [];

    /// <summary>Parsed Idle Animation (IDLE) records.</summary>
    public List<IdleAnimationRecord> IdleAnimations { get; init; } = [];

    /// <summary>Parsed Camera Path (CPTH) records.</summary>
    public List<CameraPathRecord> CameraPaths { get; init; } = [];

    /// <summary>Parsed Impact Data (IPCT) records.</summary>
    public List<ImpactDataRecord> ImpactData { get; init; } = [];

    /// <summary>Parsed Audio Location Controller (ALOC) records.</summary>
    public List<AudioLocationControllerRecord> AudioLocationControllers { get; init; } = [];

    /// <summary>Parsed Placed Grenade (PGRE) records (ESM-side only).</summary>
    public List<PlacedGrenadeRecord> PlacedGrenades { get; init; } = [];

    /// <summary>Parsed Region (REGN) records.</summary>
    public List<RegionRecord> Regions { get; init; } = [];

    /// <summary>Parsed Caravan Card (CCRD) records.</summary>
    public List<CaravanCardRecord> CaravanCards { get; init; } = [];

    /// <summary>Parsed Caravan Money (CMNY) records.</summary>
    public List<CaravanMoneyRecord> CaravanMoney { get; init; } = [];

    /// <summary>Parsed Debris (DEBR) records.</summary>
    public List<DebrisRecord> Debris { get; init; } = [];

    /// <summary>Parsed Ingredient (INGR) records.</summary>
    public List<IngredientRecord> Ingredients { get; init; } = [];

    /// <summary>Parsed NavMesh Info Map (NAVI) records.</summary>
    public List<NavMeshInfoMapRecord> NavMeshInfoMaps { get; init; } = [];

    /// <summary>Parsed Caravan Deck (CDCK) records.</summary>
    public List<CaravanDeckRecord> CaravanDecks { get; init; } = [];

    /// <summary>Parsed Radiation Stage (RADS) records.</summary>
    public List<SurvivalStageRecord> RadiationStages { get; init; } = [];

    /// <summary>Parsed Dehydration Stage (DEHY) records.</summary>
    public List<SurvivalStageRecord> DehydrationStages { get; init; } = [];

    /// <summary>Parsed Hunger Stage (HUNG) records.</summary>
    public List<SurvivalStageRecord> HungerStages { get; init; } = [];

    /// <summary>Parsed Sleep Deprivation Stage (SLPD) records.</summary>
    public List<SurvivalStageRecord> SleepDeprivationStages { get; init; } = [];

    /// <summary>Parsed Form ID List (FLST) records.</summary>
    public List<FormListRecord> FormLists { get; init; } = [];

    /// <summary>Parsed Activator (ACTI) records.</summary>
    public List<ActivatorRecord> Activators { get; init; } = [];

    /// <summary>Parsed Light (LIGH) records.</summary>
    public List<LightRecord> Lights { get; init; } = [];

    /// <summary>Parsed Door (DOOR) records.</summary>
    public List<DoorRecord> Doors { get; init; } = [];

    /// <summary>Parsed Static (STAT) records.</summary>
    public List<StaticRecord> Statics { get; init; } = [];

    /// <summary>Parsed Static Collection (SCOL) records.</summary>
    public List<StaticCollectionRecord> StaticCollections { get; init; } = [];

    /// <summary>Parsed Furniture (FURN) records.</summary>
    public List<FurnitureRecord> Furniture { get; init; } = [];

    // AI
    /// <summary>Parsed AI Package (PACK) records.</summary>
    public List<PackageRecord> Packages { get; init; } = [];

    // Generic
    /// <summary>Generic ESM records for types without specialized models (MSTT, TACT, CAMS, ANIO, etc.).</summary>
    public List<GenericEsmRecord> GenericRecords { get; init; } = [];

    // Specialized record models
    /// <summary>Parsed Sound (SOUN) records.</summary>
    public List<SoundRecord> Sounds { get; init; } = [];

    /// <summary>Parsed Music Type (MUSC) records.</summary>
    public List<MusicTypeRecord> MusicTypes { get; init; } = [];

    /// <summary>Parsed Texture Set (TXST) records.</summary>
    public List<TextureSetRecord> TextureSets { get; init; } = [];

    /// <summary>
    ///     Parsed Material Swap (MSWP) records — FO4/FO76 only, empty for earlier games. Referenced
    ///     from placements via the REFR <c>XMSP</c> FormID; the 3D viewer applies each record's
    ///     BNAM→SNAM material substitutions when decoding that placement's mesh.
    /// </summary>
    public List<MaterialSwapRecord> MaterialSwaps { get; init; } = [];

    /// <summary>Parsed Landscape Texture (LTEX) records.</summary>
    public List<LandscapeTextureRecord> LandTextures { get; init; } = [];

    /// <summary>Parsed Grass (GRAS) records — referenced by LTEX GNAM FormIDs.</summary>
    public List<GrassRecord> Grasses { get; init; } = [];

    /// <summary>Parsed Armor Addon (ARMA) records.</summary>
    public List<ArmaRecord> ArmorAddons { get; init; } = [];

    /// <summary>Parsed Water (WATR) records.</summary>
    public List<WaterRecord> Water { get; init; } = [];

    /// <summary>Parsed Body Part Data (BPTD) records.</summary>
    public List<BodyPartDataRecord> BodyPartData { get; init; } = [];

    /// <summary>Parsed Actor Value Info (AVIF) records.</summary>
    public List<ActorValueInfoRecord> ActorValueInfos { get; init; } = [];

    /// <summary>Parsed Combat Style (CSTY) records.</summary>
    public List<CombatStyleRecord> CombatStyles { get; init; } = [];

    /// <summary>Parsed Lighting Template (LGTM) records.</summary>
    public List<LightingTemplateRecord> LightingTemplates { get; init; } = [];

    /// <summary>Parsed Navigation Mesh (NAVM) records.</summary>
    public List<NavMeshRecord> NavMeshes { get; init; } = [];

    /// <summary>Parsed Weather (WTHR) records.</summary>
    public List<WeatherRecord> Weather { get; init; } = [];

    /// <summary>Parsed Climate (CLMT) records.</summary>
    public List<ClimateRecord> Climate { get; init; } = [];

    /// <summary>Parsed Image Space (IMGS) records (per-cell/worldspace post-process parameters).</summary>
    public List<ImageSpaceRecord> ImageSpaces { get; init; } = [];

    /// <summary>
    ///     FormID → model path (.nif) mapping from STAT, ACTI, DOOR, LIGH, FURN, WEAP, ARMO, AMMO, ALCH, MISC, BOOK, CONT
    ///     records.
    /// </summary>
    public Dictionary<uint, string> ModelPathIndex { get; init; } = [];

    /// <summary>
    ///     Base-object FormID → its <c>MODS</c> ("Alternate Textures") entries (shape name → TXST
    ///     FormID → 3D index). Present on material-swapped statics (billboards, signs, re-skinned
    ///     props); empty for most records. Resolved to actual TXST texture paths at world-view build
    ///     time (<c>WorldMapOverlayBuilder</c>) for the 3D viewer's per-placement re-skin.
    /// </summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<AlternateTextureEntry>> AlternateTexturesByFormId { get; init; } =
        new Dictionary<uint, IReadOnlyList<AlternateTextureEntry>>();

    /// <summary>
    ///     Base-object FormID → its default Material Swap (MSWP) FormID, from the FO4-family
    ///     <c>MODS</c> subrecord (a bare FormID there, unlike the FNV/Skyrim alternate-texture
    ///     array). Applied to every placement of the base that doesn't carry its own REFR
    ///     <c>XMSP</c> override. Empty for earlier games.
    /// </summary>
    public IReadOnlyDictionary<uint, uint> BaseMaterialSwapFormIds { get; init; } =
        new Dictionary<uint, uint>();

    /// <summary>
    ///     Base-object FormID → its <c>MODC</c> "Color Remapping Index" (0–1), FO4-family only. The
    ///     engine overrides a grayscale-to-palette material's <c>GradientMapV</c> row with this
    ///     per-base float (fo76utils render.cpp) — it is how one crate NIF + one BGSM yields the
    ///     Gray/Yellow/Blue shipping-crate colorways. Empty for earlier games.
    /// </summary>
    public IReadOnlyDictionary<uint, float> BaseColorRemapIndices { get; init; } =
        new Dictionary<uint, float>();

    /// <summary>FormID to Editor ID mapping built during parsing.</summary>
    public Dictionary<uint, string> FormIdToEditorId { get; init; } = [];

    /// <summary>FormID to display name (FullName) mapping built from runtime hash table entries.</summary>
    public Dictionary<uint, string> FormIdToDisplayName { get; init; } = [];

    /// <summary>
    ///     Runtime worldspace cell maps captured by walking TESWorldSpace pCellMap hash tables
    ///     during DMP analysis. Keyed by worldspace FormID. Empty for ESM-only sources.
    ///     Preserves the same data the runtime parser used to build cell stubs so diagnostic
    ///     consumers can inspect grid bounds and the persistent-cell pointer without re-scanning.
    /// </summary>
    public Dictionary<uint, RuntimeWorldspaceData> RuntimeWorldspaceMaps { get; init; } = [];

    /// <summary>Total records processed.</summary>
    public int TotalRecordsProcessed { get; init; }

    /// <summary>
    ///     True when this collection was parsed from a Morrowind (TES3) plugin. TES3 carries no real
    ///     FormIDs, so the parser assigns file-local synthetic ones; the load-order merge keys on FormID,
    ///     which would make one plugin's synthetic id collide with an unrelated record in another. This
    ///     flag tells the merge helpers to namespace each source's synthetic IDs by load order first
    ///     (see <c>Tes3LoadOrderNamespacer</c>). Propagated through <see cref="MergeWith" />.
    /// </summary>
    public bool IsTes3 { get; init; }

    /// <summary>
    ///     Schema-decoded field trees keyed by record FormID, for games that carry typed models AND a
    ///     registered schema (FNV/FO3). The schema-primary games (Oblivion→FO76) instead carry their
    ///     <see cref="DecodedNode" /> tree directly on each <see cref="GenericEsmRecord.DecodedTree" />;
    ///     for the typed-primary games the typed model is the record, so the parallel tree lives here.
    ///     This is the common substrate the unified, profile-driven record presentation reads from.
    ///     Empty for games without a registered schema.
    /// </summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<DecodedNode>> DecodedTreesByFormId { get; init; } =
        new Dictionary<uint, IReadOnlyList<DecodedNode>>();

    /// <summary>
    ///     The game this collection was parsed from. Needed by the unified, profile-driven record
    ///     presentation (a profile reads game-specific layouts out of the DecodedTree). Defaults to
    ///     <see cref="BethesdaGame.Unknown" />; set by the parser from its detected game.
    /// </summary>
    public BethesdaGame Game { get; init; } = BethesdaGame.Unknown;

    /// <summary>Number of records successfully parsed.</summary>
    public int TotalRecordsParsed =>
        Npcs.Count + Creatures.Count + Races.Count + Factions.Count + EncounterZones.Count +
        Quests.Count + DialogTopics.Count + Dialogues.Count + Notes.Count + Books.Count + Terminals.Count +
        Scripts.Count +
        Weapons.Count + Armor.Count + Ammo.Count + Consumables.Count + MiscItems.Count + Keys.Count + Containers.Count +
        Perks.Count + Spells.Count + Cells.Count + Worldspaces.Count + MapMarkers.Count + LeveledLists.Count +
        GameSettings.Count + Globals.Count + Enchantments.Count + BaseEffects.Count +
        WeaponMods.Count + Recipes.Count + RecipeCategories.Count + ConstructibleObjects.Count +
        Challenges.Count + Reputations.Count +
        Projectiles.Count + Explosions.Count + Messages.Count + Classes.Count +
        Eyes.Count + Hair.Count + HeadParts.Count + VoiceTypes.Count + MenuIcons.Count + LoadScreenTypes.Count +
        IdleAnimations.Count + CameraPaths.Count + ImpactData.Count + AudioLocationControllers.Count +
        PlacedGrenades.Count + Regions.Count + CaravanCards.Count + Debris.Count +
        CaravanMoney.Count + Ingredients.Count + NavMeshInfoMaps.Count + CaravanDecks.Count +
        RadiationStages.Count + DehydrationStages.Count + HungerStages.Count + SleepDeprivationStages.Count +
        FormLists.Count + Activators.Count +
        Lights.Count + Doors.Count + Statics.Count + StaticCollections.Count + Furniture.Count +
        Packages.Count +
        GenericRecords.Count +
        Sounds.Count + MusicTypes.Count + TextureSets.Count + MaterialSwaps.Count + LandTextures.Count + Grasses.Count + ArmorAddons.Count + Water.Count +
        BodyPartData.Count + ActorValueInfos.Count + CombatStyles.Count +
        LightingTemplates.Count + NavMeshes.Count + Weather.Count;

    /// <summary>
    ///     Counts of record types that were detected but not fully parsed.
    ///     Used for the "Other Records" summary section in split reports.
    /// </summary>
    public Dictionary<string, int> UnparsedTypeCounts { get; init; } = [];

    /// <summary>
    ///     Creates a new RecordCollection by merging this collection with records from
    ///     another collection. For duplicate FormIDs, records from <paramref name="overlay" />
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
            EncounterZones = MergeList(EncounterZones, overlay.EncounterZones, r => r.FormId),

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
            Cells = MergeCells(Cells, overlay.Cells),
            Worldspaces = MergeWorldspaces(Worldspaces, overlay.Worldspaces),
            MapMarkers = MergeList(MapMarkers, overlay.MapMarkers, r => r.FormId),
            LeveledLists = MergeList(LeveledLists, overlay.LeveledLists, r => r.FormId),

            // Game Data
            GameSettings = MergeList(GameSettings, overlay.GameSettings, r => r.FormId),
            Globals = MergeList(Globals, overlay.Globals, r => r.FormId),
            Enchantments = MergeList(Enchantments, overlay.Enchantments, r => r.FormId),
            BaseEffects = MergeList(BaseEffects, overlay.BaseEffects, r => r.FormId),
            WeaponMods = MergeList(WeaponMods, overlay.WeaponMods, r => r.FormId),
            Recipes = MergeList(Recipes, overlay.Recipes, r => r.FormId),
            RecipeCategories = MergeList(RecipeCategories, overlay.RecipeCategories, r => r.FormId),
            ConstructibleObjects = MergeList(ConstructibleObjects, overlay.ConstructibleObjects, r => r.FormId),
            Challenges = MergeList(Challenges, overlay.Challenges, r => r.FormId),
            Reputations = MergeList(Reputations, overlay.Reputations, r => r.FormId),
            Projectiles = MergeList(Projectiles, overlay.Projectiles, r => r.FormId),
            Explosions = MergeList(Explosions, overlay.Explosions, r => r.FormId),
            Messages = MergeList(Messages, overlay.Messages, r => r.FormId),
            Classes = MergeList(Classes, overlay.Classes, r => r.FormId),
            HeadParts = MergeList(HeadParts, overlay.HeadParts, r => r.FormId),
            VoiceTypes = MergeList(VoiceTypes, overlay.VoiceTypes, r => r.FormId),
            MenuIcons = MergeList(MenuIcons, overlay.MenuIcons, r => r.FormId),
            LoadScreenTypes = MergeList(LoadScreenTypes, overlay.LoadScreenTypes, r => r.FormId),
            IdleAnimations = MergeList(IdleAnimations, overlay.IdleAnimations, r => r.FormId),
            CameraPaths = MergeList(CameraPaths, overlay.CameraPaths, r => r.FormId),
            ImpactData = MergeList(ImpactData, overlay.ImpactData, r => r.FormId),
            AudioLocationControllers =
                MergeList(AudioLocationControllers, overlay.AudioLocationControllers, r => r.FormId),
            PlacedGrenades = MergeList(PlacedGrenades, overlay.PlacedGrenades, r => r.FormId),
            Regions = MergeList(Regions, overlay.Regions, r => r.FormId),
            CaravanCards = MergeList(CaravanCards, overlay.CaravanCards, r => r.FormId),
            CaravanMoney = MergeList(CaravanMoney, overlay.CaravanMoney, r => r.FormId),
            Debris = MergeList(Debris, overlay.Debris, r => r.FormId),
            Ingredients = MergeList(Ingredients, overlay.Ingredients, r => r.FormId),
            NavMeshInfoMaps = MergeList(NavMeshInfoMaps, overlay.NavMeshInfoMaps, r => r.FormId),
            CaravanDecks = MergeList(CaravanDecks, overlay.CaravanDecks, r => r.FormId),
            RadiationStages = MergeList(RadiationStages, overlay.RadiationStages, r => r.FormId),
            DehydrationStages = MergeList(DehydrationStages, overlay.DehydrationStages, r => r.FormId),
            HungerStages = MergeList(HungerStages, overlay.HungerStages, r => r.FormId),
            SleepDeprivationStages =
                MergeList(SleepDeprivationStages, overlay.SleepDeprivationStages, r => r.FormId),
            FormLists = MergeList(FormLists, overlay.FormLists, r => r.FormId),
            Activators = MergeList(Activators, overlay.Activators, r => r.FormId),
            Lights = MergeList(Lights, overlay.Lights, r => r.FormId),
            Doors = MergeList(Doors, overlay.Doors, r => r.FormId),
            Statics = MergeList(Statics, overlay.Statics, r => r.FormId),
            StaticCollections = MergeList(StaticCollections, overlay.StaticCollections, r => r.FormId),
            Furniture = MergeList(Furniture, overlay.Furniture, r => r.FormId),

            // AI
            Packages = MergeList(Packages, overlay.Packages, r => r.FormId),

            // Generic
            GenericRecords = MergeList(GenericRecords, overlay.GenericRecords, r => r.FormId),

            // Specialized
            Sounds = MergeList(Sounds, overlay.Sounds, r => r.FormId),
            MusicTypes = MergeList(MusicTypes, overlay.MusicTypes, r => r.FormId),
            TextureSets = MergeList(TextureSets, overlay.TextureSets, r => r.FormId),
            MaterialSwaps = MergeList(MaterialSwaps, overlay.MaterialSwaps, r => r.FormId),
            LandTextures = MergeList(LandTextures, overlay.LandTextures, r => r.FormId),
            Grasses = MergeList(Grasses, overlay.Grasses, r => r.FormId),
            ArmorAddons = MergeList(ArmorAddons, overlay.ArmorAddons, r => r.FormId),
            Water = MergeList(Water, overlay.Water, r => r.FormId),
            BodyPartData = MergeList(BodyPartData, overlay.BodyPartData, r => r.FormId),
            ActorValueInfos = MergeList(ActorValueInfos, overlay.ActorValueInfos, r => r.FormId),
            CombatStyles = MergeList(CombatStyles, overlay.CombatStyles, r => r.FormId),
            LightingTemplates = MergeList(LightingTemplates, overlay.LightingTemplates, r => r.FormId),
            NavMeshes = MergeList(NavMeshes, overlay.NavMeshes, r => r.FormId),
            Weather = MergeList(Weather, overlay.Weather, r => r.FormId),
            Climate = MergeList(Climate, overlay.Climate, r => r.FormId),
            ImageSpaces = MergeList(ImageSpaces, overlay.ImageSpaces, r => r.FormId),

            // Dictionaries: overlay overwrites base
            ModelPathIndex = MergeDictionary(ModelPathIndex, overlay.ModelPathIndex),
            FormIdToEditorId = MergeDictionary(FormIdToEditorId, overlay.FormIdToEditorId),
            FormIdToDisplayName = MergeDictionary(FormIdToDisplayName, overlay.FormIdToDisplayName),
            RuntimeWorldspaceMaps = MergeDictionary(RuntimeWorldspaceMaps, overlay.RuntimeWorldspaceMaps),
            UnparsedTypeCounts = MergeDictionary(UnparsedTypeCounts, overlay.UnparsedTypeCounts),
            AlternateTexturesByFormId = MergeDictionary(
                new Dictionary<uint, IReadOnlyList<AlternateTextureEntry>>(AlternateTexturesByFormId),
                new Dictionary<uint, IReadOnlyList<AlternateTextureEntry>>(overlay.AlternateTexturesByFormId)),
            BaseMaterialSwapFormIds = MergeDictionary(
                new Dictionary<uint, uint>(BaseMaterialSwapFormIds),
                new Dictionary<uint, uint>(overlay.BaseMaterialSwapFormIds)),
            BaseColorRemapIndices = MergeDictionary(
                new Dictionary<uint, float>(BaseColorRemapIndices),
                new Dictionary<uint, float>(overlay.BaseColorRemapIndices)),

            TotalRecordsProcessed = TotalRecordsProcessed + overlay.TotalRecordsProcessed,
            IsTes3 = IsTes3 || overlay.IsTes3,
            Game = Game != BethesdaGame.Unknown ? Game : overlay.Game,
            DecodedTreesByFormId = MergeDictionary(
                new Dictionary<uint, IReadOnlyList<DecodedNode>>(DecodedTreesByFormId),
                new Dictionary<uint, IReadOnlyList<DecodedNode>>(overlay.DecodedTreesByFormId))
        };
    }

    /// <summary>
    ///     Rebuilds every <see cref="WorldspaceRecord.Cells" /> list from this collection's (possibly
    ///     merged) top-level <see cref="Cells" />, grouped by <see cref="CellRecord.WorldspaceFormId" />.
    ///     Call after <see cref="MergeWith" />: that method merges the flat <see cref="Cells" /> and
    ///     <see cref="Worldspaces" /> lists by FormID but leaves each worldspace pointing at the cells
    ///     it was linked with at its <em>own</em> parse time, so an overridden/added cell never reaches
    ///     consumers that read <c>ws.Cells</c> (the 3D viewer, the 2D overlay builder). Each
    ///     worldspace's list is reset first so a fully-overridden worldspace cannot retain stale
    ///     references. Mirrors <c>CellLinkageHandler.LinkCellsToWorldspaces</c> but clear-first.
    ///     Mutates this collection's <see cref="Worldspaces" /> list in place (replacing slots with
    ///     <c>ws with { Cells = … }</c>; the shared source records are never mutated) and returns this.
    /// </summary>
    public RecordCollection RelinkWorldspaceCells()
    {
        if (Worldspaces.Count == 0)
        {
            return this;
        }

        var indexByFormId = new Dictionary<uint, int>(Worldspaces.Count);
        for (var i = 0; i < Worldspaces.Count; i++)
        {
            indexByFormId.TryAdd(Worldspaces[i].FormId, i);
        }

        var cellsByWorldspace = new Dictionary<uint, List<CellRecord>>();
        foreach (var cell in Cells)
        {
            if (cell.WorldspaceFormId is > 0 && indexByFormId.ContainsKey(cell.WorldspaceFormId.Value))
            {
                if (!cellsByWorldspace.TryGetValue(cell.WorldspaceFormId.Value, out var list))
                {
                    list = [];
                    cellsByWorldspace[cell.WorldspaceFormId.Value] = list;
                }

                list.Add(cell);
            }
        }

        for (var i = 0; i < Worldspaces.Count; i++)
        {
            var ws = Worldspaces[i];
            var cells = cellsByWorldspace.TryGetValue(ws.FormId, out var list) ? list : [];
            ws = ws with { Cells = cells };

            // The single shared TES3 exterior worldspace is the merge of every plugin's "Wilderness";
            // MergeWith keeps only the last plugin's map corners, so recompute them to span the unioned
            // cells (otherwise Solstheim or Vvardenfell gets clipped off the 2D map). Other games' WRLD
            // bounds are authoritative and left untouched.
            if (ws.FormId == WorldspaceRecord.Tes3SyntheticExteriorFormId)
            {
                ws = ws.WithMorrowindExteriorBounds(cells);
            }

            Worldspaces[i] = ws;
        }

        return this;
    }

    /// <summary>
    ///     Resolves the mesh for any placed reference left without a <see cref="PlacedReference.ModelPath" />
    ///     after a load-order merge, against the MERGED base set. The per-source enrichment that bakes a
    ///     ref's model (<c>ObjectIndexBuilder.BuildAndEnrich</c> for TES4+, <c>Tes3RecordParser</c> for
    ///     TES3) only ever sees that source's OWN records, and <see cref="MergeWith" /> unions the records +
    ///     <see cref="ModelPathIndex" /> but never re-resolves the placed refs — so a ref that places a base
    ///     defined in another loaded plugin (e.g. a Bloodmoon REFR placing a Morrowind Imperial-fort STAT in
    ///     Fort Frostmoth, or a TES4 mod placing a vanilla static) is left with a null ModelPath and is then
    ///     silently dropped by the renderer (<c>RenderableReference.TryBuild</c>) — it renders "missing
    ///     entirely". This pass closes that gap for every game:
    ///     <list type="number">
    ///       <item>by <see cref="PlacedReference.BaseFormId" /> through the merged
    ///       <see cref="ModelPathIndex" /> (TES4+ references carry real, cross-plugin-stable FormIDs); then</item>
    ///       <item>by <see cref="PlacedReference.BaseEditorId" /> for refs whose cross-plugin FormID is
    ///       unresolved — TES3 references are editor-id strings, so a master-defined base leaves
    ///       <c>BaseFormId == 0</c> — which also backfills the FormID.</item>
    ///     </list>
    ///     Call after <see cref="MergeWith" />; mutates each cell's <see cref="CellRecord.PlacedObjects" />
    ///     list in place (replacing entries via <c>with</c>; the shared base records are never mutated).
    /// </summary>
    public RecordCollection ResolvePlacedModels()
    {
        if (Cells.Count == 0)
        {
            return this;
        }

        // editor-id → (FormId, model) over the merged base set, for string-keyed refs whose per-plugin
        // FormID didn't resolve cross-plugin (TES3). Built lazily: TES4+ refs resolve by FormID first and
        // never need it. TES3 routes every base into GenericRecords, which carry both the id and the MODL.
        Dictionary<string, (uint FormId, string Model)>? byEditorId = null;

        foreach (var cell in Cells)
        {
            var refs = cell.PlacedObjects;
            for (var i = 0; i < refs.Count; i++)
            {
                var p = refs[i];
                if (!string.IsNullOrEmpty(p.ModelPath))
                {
                    continue;
                }

                // 1) FormID → model via the merged index (all games; covers cross-plugin overlays).
                if (p.BaseFormId != 0 && ModelPathIndex.TryGetValue(p.BaseFormId, out var model))
                {
                    refs[i] = p with { ModelPath = model };
                    continue;
                }

                // 2) editor-id → model, for refs whose base FormID is unresolved cross-plugin (TES3).
                if (string.IsNullOrEmpty(p.BaseEditorId))
                {
                    continue;
                }

                byEditorId ??= BuildGenericEditorIdModelMap();
                if (byEditorId.TryGetValue(p.BaseEditorId!, out var found))
                {
                    refs[i] = p with
                    {
                        ModelPath = found.Model,
                        BaseFormId = p.BaseFormId == 0 ? found.FormId : p.BaseFormId
                    };
                }
            }
        }

        return this;
    }

    // editor-id → (FormId, model) from the generic base records, last-wins so a higher-priority plugin's
    // override of a base's mesh takes effect. Used by ResolvePlacedModels for TES3 string-keyed refs.
    private Dictionary<string, (uint FormId, string Model)> BuildGenericEditorIdModelMap()
    {
        var map = new Dictionary<string, (uint, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in GenericRecords)
        {
            if (!string.IsNullOrEmpty(rec.EditorId) && !string.IsNullOrEmpty(rec.ModelPath))
            {
                map[rec.EditorId!] = (rec.FormId, rec.ModelPath!);
            }
        }

        return map;
    }

    /// <summary>
    ///     Returns a copy whose <see cref="Worldspaces" /> contains only entries whose FormID is in
    ///     <paramref name="keep" />. Scopes a DMP view to the worldspaces actually captured in the dump:
    ///     Load-Order ESM worldspaces are still merged in (their cells/terrain back the captured
    ///     worldspaces) but must not appear in the worldspace picker. All other collections (cells,
    ///     markers, terrain) are shared by reference, so navigation and interiors are unaffected.
    /// </summary>
    public RecordCollection WithWorldspacesFilteredTo(IReadOnlySet<uint> keep)
    {
        if (Worldspaces.Count == 0 || Worldspaces.All(w => keep.Contains(w.FormId)))
        {
            return this;
        }

        return this with { Worldspaces = Worldspaces.Where(w => keep.Contains(w.FormId)).ToList() };
    }

    /// <summary>
    ///     Returns a copy whose <see cref="Cells" /> contains only entries whose FormID is in
    ///     <paramref name="keep" />. Scopes a DMP view to the cells actually captured in the dump so the
    ///     Load-Order ESM cells — merged in only to back base-record/texture/asset resolution — don't
    ///     gap-fill the viewer's cell grid and lists. Call <see cref="RelinkWorldspaceCells" /> afterward
    ///     so each worldspace's Cells reflects the trimmed set. All other collections (statics, textures,
    ///     resolvers) stay shared by reference, so dumped objects still resolve their models/textures.
    /// </summary>
    public RecordCollection WithCellsFilteredTo(IReadOnlySet<uint> keep)
    {
        if (Cells.Count == 0 || Cells.All(c => keep.Contains(c.FormId)))
        {
            return this;
        }

        return this with { Cells = Cells.Where(c => keep.Contains(c.FormId)).ToList() };
    }

    /// <summary>Creates a FormIdResolver from this collection's dictionaries.</summary>
    public FormIdResolver CreateResolver(Dictionary<uint, string>? overrideEditorIds = null)
    {
        var resolver = new FormIdResolver(
            overrideEditorIds ?? FormIdToEditorId,
            FormIdToDisplayName,
            BuildRefToBaseMap(),
            BuildActorValueNames());

        // Detect skill era from AVIF records and weapon Skill field values.
        if (ActorValueInfos.Count > 0 || Weapons.Count > 0)
        {
            resolver.SkillEra = SkillEraDetector.Detect(this);
        }

        return resolver;
    }

    /// <summary>
    ///     Builds an actor value name array from parsed AVIF records.
    ///     ESM GRUP records arrive in AV-code order (list index = AV code), but runtime-merged
    ///     records from DMP files arrive in arbitrary memory scan order.
    ///     Uses the EditorID → AV code mapping to correctly position each record.
    ///     Returns null if no AVIF records are available.
    /// </summary>
    private string?[]? BuildActorValueNames()
    {
        if (ActorValueInfos.Count == 0)
        {
            return null;
        }

        // Determine the max AV code to size the array
        var maxAvCode = 0;
        foreach (var avif in ActorValueInfos)
        {
            if (avif.EditorId != null &&
                FormIdResolver.AvifEditorIdToAvCode.TryGetValue(avif.EditorId, out var code) &&
                code > maxAvCode)
            {
                maxAvCode = code;
            }
        }

        // If no EditorIDs matched, fall back to positional indexing (ESM GRUP order)
        if (maxAvCode == 0)
        {
            var names = new string[ActorValueInfos.Count];
            for (var i = 0; i < ActorValueInfos.Count; i++)
            {
                names[i] = ActorValueInfos[i].FullName ?? ActorValueInfos[i].EditorId ?? $"AV#{i}";
            }

            return names;
        }

        // Build array indexed by AV code using EditorID mapping
        var result = new string?[maxAvCode + 1];
        foreach (var avif in ActorValueInfos)
        {
            if (avif.EditorId != null &&
                FormIdResolver.AvifEditorIdToAvCode.TryGetValue(avif.EditorId, out var avCode))
            {
                // Only store FullName (from BSStringT or ESM FULL subrecord).
                // Null slots fall through to FallbackSkillNames in GetActorValueName().
                result[avCode] = avif.FullName;
            }
        }

        return result;
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

    /// <summary>
    ///     Builds a reverse index: faction FormID -> list of (NPC FormID, NPC name) members.
    ///     Used for displaying faction membership in the data browser.
    /// </summary>
    public Dictionary<uint, List<(uint FormId, string? Name)>> BuildFactionMembersIndex()
    {
        var map = new Dictionary<uint, List<(uint, string?)>>();

        void AddMembers(uint npcFormId, string? npcName, List<FactionMembership> factions)
        {
            foreach (var fm in factions)
            {
                if (fm.FactionFormId == 0)
                {
                    continue;
                }

                if (!map.TryGetValue(fm.FactionFormId, out var members))
                {
                    members = [];
                    map[fm.FactionFormId] = members;
                }

                members.Add((npcFormId, npcName));
            }
        }

        foreach (var npc in Npcs)
        {
            AddMembers(npc.FormId, npc.FullName ?? npc.EditorId, npc.Factions);
        }

        foreach (var crea in Creatures)
        {
            AddMembers(crea.FormId, crea.FullName ?? crea.EditorId, crea.Factions);
        }

        return map;
    }

    /// <summary>
    ///     Builds a reverse index: key FormID → list of locked doors/containers that use this key.
    ///     Each entry includes the placed reference, its containing cell, and lock level.
    /// </summary>
    public Dictionary<uint, List<(PlacedReference Ref, CellRecord Cell)>> BuildKeyToLockedDoorsMap()
    {
        var map = new Dictionary<uint, List<(PlacedReference, CellRecord)>>();

        foreach (var cell in Cells)
        {
            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.LockKeyFormId is not > 0)
                {
                    continue;
                }

                if (!map.TryGetValue(obj.LockKeyFormId.Value, out var list))
                {
                    list = [];
                    map[obj.LockKeyFormId.Value] = list;
                }

                list.Add((obj, cell));
            }
        }

        return map;
    }

    /// <summary>
    ///     Builds a reverse index: IMOD FormID → list of (weapon, slot) that accept this mod.
    ///     Used for displaying what a weapon mod does in the weapon mod report.
    /// </summary>
    public Dictionary<uint, List<(WeaponRecord Weapon, WeaponModSlot Slot)>> BuildModToWeaponMap()
    {
        var map = new Dictionary<uint, List<(WeaponRecord, WeaponModSlot)>>();

        foreach (var weapon in Weapons)
        {
            foreach (var slot in weapon.ModSlots)
            {
                if (slot.ModFormId is not > 0)
                {
                    continue;
                }

                if (!map.TryGetValue(slot.ModFormId.Value, out var list))
                {
                    list = [];
                    map[slot.ModFormId.Value] = list;
                }

                list.Add((weapon, slot));
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
    ///     Merges two lists, deduplicating by FormID. Items from <paramref name="overlay" />
    ///     take precedence over items from <paramref name="baseList" /> for the same FormID.
    /// </summary>
    /// <summary>
    ///     Worldspace-specific merge. WRLD overrides in a load order are ADDITIVE for children: a
    ///     DLC's override of Commonwealth carries only ITS added cells, so the generic
    ///     overlay-wins-wholesale <see cref="MergeList{T}" /> replaced the base game's 36,864-cell
    ///     record with the DLC's 72-cell override — merged FO4 Data-dir loads rendered a
    ///     near-empty Commonwealth. The override still wins the record's scalar fields (name,
    ///     bounds, water), but the two records' cell children are UNIONED by FormID, with the
    ///     override's version of a colliding cell kept.
    /// </summary>
    private static List<WorldspaceRecord> MergeWorldspaces(
        List<WorldspaceRecord> baseList,
        List<WorldspaceRecord> overlay)
    {
        if (baseList.Count == 0) return new List<WorldspaceRecord>(overlay);
        if (overlay.Count == 0) return new List<WorldspaceRecord>(baseList);

        var baseByFormId = new Dictionary<uint, WorldspaceRecord>(baseList.Count);
        foreach (var ws in baseList)
        {
            baseByFormId.TryAdd(ws.FormId, ws);
        }

        var overlayIds = new HashSet<uint>(overlay.Select(w => w.FormId));
        var merged = new List<WorldspaceRecord>(baseList.Count + overlay.Count);
        foreach (var ws in baseList)
        {
            if (!overlayIds.Contains(ws.FormId))
            {
                merged.Add(ws);
            }
        }

        foreach (var ws in overlay)
        {
            if (!baseByFormId.TryGetValue(ws.FormId, out var baseWs) || baseWs.Cells.Count == 0)
            {
                merged.Add(ws);
                continue;
            }

            // Same child-merge semantics as MergeCells: a cell present in both files folds via
            // MergeCellPair (base LAND/REFRs survive an override that doesn't re-ship them). This
            // list is what consumers that never call RelinkWorldspaceCells read (ws.Cells), so it
            // must not keep the bare override instances.
            var baseCellsByFormId = new Dictionary<uint, CellRecord>(baseWs.Cells.Count);
            foreach (var cell in baseWs.Cells)
            {
                if (cell.FormId != 0)
                {
                    baseCellsByFormId.TryAdd(cell.FormId, cell);
                }
            }

            var seen = new HashSet<uint>(ws.Cells.Select(c => c.FormId));
            var cells = new List<CellRecord>(ws.Cells.Count + baseWs.Cells.Count);
            foreach (var cell in ws.Cells)
            {
                cells.Add(cell.FormId != 0 && baseCellsByFormId.TryGetValue(cell.FormId, out var baseCell)
                    ? MergeCellPair(baseCell, cell)
                    : cell);
            }

            foreach (var cell in baseWs.Cells)
            {
                if (seen.Add(cell.FormId))
                {
                    cells.Add(cell);
                }
            }

            merged.Add(ws with { Cells = cells });
        }

        return merged;
    }

    /// <summary>
    ///     Merges cell lists with engine load-order semantics. A CELL override record in a later file
    ///     carries only that file's header fields plus the children it adds or changes — cell CHILDREN
    ///     merge across files, they are not replaced. FO4's DLCs re-ship CELL headers for thousands of
    ///     Commonwealth cells (precombine/previs regeneration) with no LAND and none of the base REFRs;
    ///     whole-record replacement therefore erased the terrain and objects of every overridden cell
    ///     (the missing downtown-Boston rectangle when all DLC ESMs are loaded).
    /// </summary>
    private static List<CellRecord> MergeCells(List<CellRecord> baseList, List<CellRecord> overlay)
    {
        if (baseList.Count == 0) return new List<CellRecord>(overlay);
        if (overlay.Count == 0) return new List<CellRecord>(baseList);

        // Later duplicates within one overlay win, mirroring MergeList's last-wins list order.
        // FormId 0 marks synthetic cells (DMP virtual buckets) — never collide those.
        var overlayByFormId = new Dictionary<uint, CellRecord>(overlay.Count);
        foreach (var cell in overlay)
        {
            if (cell.FormId != 0)
            {
                overlayByFormId[cell.FormId] = cell;
            }
        }

        var merged = new List<CellRecord>(baseList.Count + overlay.Count);
        var consumed = new HashSet<uint>();
        foreach (var baseCell in baseList)
        {
            if (baseCell.FormId != 0 && overlayByFormId.TryGetValue(baseCell.FormId, out var overrideCell))
            {
                merged.Add(MergeCellPair(baseCell, overrideCell));
                consumed.Add(baseCell.FormId);
            }
            else
            {
                merged.Add(baseCell);
            }
        }

        foreach (var cell in overlay)
        {
            // Skip only the single dictionary-winning instance of a consumed FormID; a stray duplicate
            // with the same FormID would be dropped too, which is the same de-dup MergeList applies.
            if (cell.FormId == 0 || !consumed.Contains(cell.FormId))
            {
                merged.Add(cell);
            }
        }

        return merged;
    }

    /// <summary>
    ///     Folds one CELL override onto its base record: override header fields win where present,
    ///     while children (placed references, LAND heightmap/visual data) merge — base children
    ///     survive unless the override re-ships them (per-REFR override by FormID).
    /// </summary>
    private static CellRecord MergeCellPair(CellRecord baseCell, CellRecord overrideCell)
    {
        List<PlacedReference> placed;
        if (baseCell.PlacedObjects.Count == 0)
        {
            placed = overrideCell.PlacedObjects;
        }
        else if (overrideCell.PlacedObjects.Count == 0)
        {
            placed = baseCell.PlacedObjects;
        }
        else
        {
            var overriddenRefs = new HashSet<uint>(overrideCell.PlacedObjects
                .Where(r => r.FormId != 0)
                .Select(r => r.FormId));
            placed = new List<PlacedReference>(baseCell.PlacedObjects.Count + overrideCell.PlacedObjects.Count);
            placed.AddRange(baseCell.PlacedObjects.Where(r => r.FormId == 0 || !overriddenRefs.Contains(r.FormId)));
            placed.AddRange(overrideCell.PlacedObjects);
        }

        var linkedCells = overrideCell.LinkedCellFormIds;
        if (baseCell.LinkedCellFormIds.Count > 0)
        {
            linkedCells = overrideCell.LinkedCellFormIds
                .Concat(baseCell.LinkedCellFormIds)
                .Distinct()
                .ToList();
        }

        return overrideCell with
        {
            EditorId = overrideCell.EditorId ?? baseCell.EditorId,
            FullName = overrideCell.FullName ?? baseCell.FullName,
            GridX = overrideCell.GridX ?? baseCell.GridX,
            GridY = overrideCell.GridY ?? baseCell.GridY,
            WorldspaceFormId = overrideCell.WorldspaceFormId ?? baseCell.WorldspaceFormId,
            CellWorldSize = overrideCell.CellWorldSize != 0f ? overrideCell.CellWorldSize : baseCell.CellWorldSize,
            WaterHeight = overrideCell.WaterHeight ?? baseCell.WaterHeight,
            EncounterZoneFormId = overrideCell.EncounterZoneFormId ?? baseCell.EncounterZoneFormId,
            MusicTypeFormId = overrideCell.MusicTypeFormId ?? baseCell.MusicTypeFormId,
            AcousticSpaceFormId = overrideCell.AcousticSpaceFormId ?? baseCell.AcousticSpaceFormId,
            ImageSpaceFormId = overrideCell.ImageSpaceFormId ?? baseCell.ImageSpaceFormId,
            LightingTemplateFormId = overrideCell.LightingTemplateFormId ?? baseCell.LightingTemplateFormId,
            LightingTemplateInheritanceFlags =
                overrideCell.LightingTemplateInheritanceFlags ?? baseCell.LightingTemplateInheritanceFlags,
            LightingData = overrideCell.LightingData ?? baseCell.LightingData,
            RadiationRegionFormIds = overrideCell.RadiationRegionFormIds.Count > 0
                ? overrideCell.RadiationRegionFormIds
                : baseCell.RadiationRegionFormIds,
            PlacedObjects = placed,
            LinkedCellFormIds = linkedCells,
            Heightmap = overrideCell.Heightmap ?? baseCell.Heightmap,
            LandVisualData = overrideCell.LandVisualData ?? baseCell.LandVisualData,
            RuntimeTerrainMesh = overrideCell.RuntimeTerrainMesh ?? baseCell.RuntimeTerrainMesh,
            HasPersistentObjects = overrideCell.HasPersistentObjects || baseCell.HasPersistentObjects,
        };
    }

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


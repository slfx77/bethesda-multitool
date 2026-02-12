using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reconstructs semantic game data from ESM record scan results and runtime memory structures.
///     Facade that delegates to domain-specific handler classes.
/// </summary>
public sealed class RecordParser
{
    #region Fields

    /// <summary>
    ///     Shared context holding scan results, accessor, and lookup tables.
    /// </summary>
    internal readonly RecordParserContext _context;

    // Domain-specific handlers
    private readonly ActorRecordHandler _actors;
    private readonly ItemRecordHandler _items;
    private readonly DialogueRecordHandler _dialogue;
    private readonly TextRecordHandler _text;
    private readonly ScriptRecordHandler _scripts;
    private readonly EffectRecordHandler _effects;
    private readonly WorldRecordHandler _world;
    private readonly MiscRecordHandler _misc;
    private readonly AiRecordHandler _ai;

    #endregion

    #region Constructor

    /// <summary>
    ///     Creates a new RecordParser with scan results and optional memory-mapped access.
    /// </summary>
    /// <param name="scanResult">The ESM record scan results from EsmRecordFormat.</param>
    /// <param name="formIdCorrelations">FormID to Editor ID correlations.</param>
    /// <param name="accessor">Optional memory-mapped accessor for reading additional record data.</param>
    /// <param name="fileSize">Size of the memory dump file.</param>
    /// <param name="minidumpInfo">Optional minidump info for runtime struct reading (pointer following).</param>
    public RecordParser(
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? formIdCorrelations = null,
        MemoryMappedViewAccessor? accessor = null,
        long fileSize = 0,
        MinidumpInfo? minidumpInfo = null)
    {
        _context = new RecordParserContext(scanResult, formIdCorrelations, accessor, fileSize, minidumpInfo);

        _actors = new ActorRecordHandler(_context);
        _items = new ItemRecordHandler(_context);
        _dialogue = new DialogueRecordHandler(_context);
        _text = new TextRecordHandler(_context);
        _scripts = new ScriptRecordHandler(_context);
        _effects = new EffectRecordHandler(_context);
        _world = new WorldRecordHandler(_context);
        _misc = new MiscRecordHandler(_context);
        _ai = new AiRecordHandler(_context);
    }

    #endregion

    #region Public API - Reconstruction

    /// <summary>
    ///     Perform full semantic reconstruction of all supported record types.
    /// </summary>
    public RecordCollection ReconstructAll(IProgress<(int percent, string phase)>? progress = null)
    {
        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

        // Reconstructed record types
        var reconstructedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NPC_", "CREA", "RACE", "FACT",
            "QUST", "DIAL", "INFO", "NOTE", "BOOK", "TERM", "SCPT",
            "WEAP", "ARMO", "AMMO", "ALCH", "MISC", "KEYM", "CONT",
            "PERK", "SPEL", "CELL", "WRLD", "GMST",
            "GLOB", "ENCH", "MGEF", "IMOD", "RCPE", "CHAL", "REPU",
            "PROJ", "EXPL", "MESG", "CLAS",
            "FLST", "ACTI", "LIGH", "DOOR", "STAT", "FURN",
            "PACK"
        };

        // Count all record types and compute unreconstructed counts
        var allTypeCounts = _context.ScanResult.MainRecords
            .GroupBy(r => r.RecordType)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var unreconstructedCounts = allTypeCounts
            .Where(kvp => !reconstructedTypes.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // Enrich LAND records with runtime cell coordinates for heightmap stitching
        if (_context.RuntimeReader != null)
        {
            var runtimeLandData = _context.RuntimeReader.ReadAllRuntimeLandData(
                _context.ScanResult.RuntimeEditorIds);
            if (runtimeLandData.Count > 0)
            {
                EsmWorldExtractor.EnrichLandRecordsWithRuntimeData(_context.ScanResult, runtimeLandData);
                Logger.Instance.Debug(
                    $"  [Semantic] Enriched {runtimeLandData.Count} LAND records with runtime cell coordinates");
            }
        }

        // Pre-scan all records for FULL subrecords (display names) so that
        // unreconstructed types (HAIR, EYES, CSTY, etc.) have names available for display
        progress?.Report((2, "Scanning display names..."));
        phaseSw.Restart();
        _context.CaptureAllFullNames();
        Logger.Instance.Debug(
            $"  [Semantic] Display names: {phaseSw.Elapsed} ({_context.FormIdToFullName.Count} names captured)");

        // Build weapons and ammo first, then cross-reference for projectile data
        progress?.Report((5, "Reconstructing characters..."));
        phaseSw.Restart();
        var npcs = _actors.ReconstructNpcs();
        var creatures = _actors.ReconstructCreatures();
        var races = _actors.ReconstructRaces();
        var factions = _actors.ReconstructFactions();
        Logger.Instance.Debug(
            $"  [Semantic] Characters: {phaseSw.Elapsed} (NPCs: {npcs.Count}, Creatures: {creatures.Count}, Races: {races.Count}, Factions: {factions.Count})");

        progress?.Report((15, "Reconstructing items..."));
        phaseSw.Restart();
        var weapons = _items.ReconstructWeapons();
        var ammo = _items.ReconstructAmmo();
        _items.EnrichAmmoWithProjectileModels(weapons, ammo);
        _items.EnrichWeaponsWithProjectileData(weapons);
        var armor = _items.ReconstructArmor();
        var consumables = _items.ReconstructConsumables();
        var miscItems = _items.ReconstructMiscItems();
        var keys = _items.ReconstructKeys();
        var containers = _items.ReconstructContainers();
        Logger.Instance.Debug(
            $"  [Semantic] Items: {phaseSw.Elapsed} (Weapons: {weapons.Count}, Armor: {armor.Count}, Ammo: {ammo.Count}, Consumables: {consumables.Count}, Misc: {miscItems.Count}, Keys: {keys.Count}, Containers: {containers.Count})");

        // Build dialogue data, then construct the tree hierarchy
        progress?.Report((30, "Reconstructing dialogue..."));
        phaseSw.Restart();
        var quests = _dialogue.ReconstructQuests();
        var dialogTopics = _dialogue.ReconstructDialogTopics();
        var dialogues = _dialogue.ReconstructDialogue();

        if (_context.RuntimeReader != null)
        {
            _dialogue.MergeRuntimeDialogueTopicLinks(dialogues, dialogTopics);
            _dialogue.MergeRuntimeDialogueData(dialogues);
        }
        else if (_context.Accessor != null)
        {
            _dialogue.LinkInfoToTopicsByGroupOrder(dialogues, dialogTopics);
        }

        DialogueRecordHandler.PropagateTopicSpeakers(dialogues, dialogTopics);
        DialogueRecordHandler.PropagateTopicSiblingSpeakers(dialogues);
        DialogueRecordHandler.PropagateQuestSpeakers(dialogues);
        DialogueRecordHandler.LinkDialogueByEditorIdConvention(dialogues, quests);
        Logger.Instance.Debug(
            $"  [Semantic] Dialogue: {phaseSw.Elapsed} (Quests: {quests.Count}, Topics: {dialogTopics.Count}, Dialogues: {dialogues.Count})");

        progress?.Report((45, "Building dialogue trees..."));
        phaseSw.Restart();
        var dialogueTree = _dialogue.BuildDialogueTrees(dialogues, dialogTopics, quests);
        var notes = _text.ReconstructNotes();
        var books = _text.ReconstructBooks();
        var terminals = _text.ReconstructTerminals();
        var scripts = _scripts.ReconstructScripts();
        Logger.Instance.Debug(
            $"  [Semantic] Trees/text: {phaseSw.Elapsed} (Notes: {notes.Count}, Books: {books.Count}, Terminals: {terminals.Count}, Scripts: {scripts.Count})");

        progress?.Report((55, "Reconstructing abilities..."));
        phaseSw.Restart();
        var perks = _effects.ReconstructPerks();
        var spells = _effects.ReconstructSpells();
        Logger.Instance.Debug(
            $"  [Semantic] Abilities: {phaseSw.Elapsed} (Perks: {perks.Count}, Spells: {spells.Count})");

        progress?.Report((60, "Reconstructing world data..."));
        phaseSw.Restart();
        var cells = _world.ReconstructCells();
        var cellTime = phaseSw.Elapsed;
        var worldspaces = _world.ReconstructWorldspaces();
        WorldRecordHandler.LinkCellsToWorldspaces(cells, worldspaces);
        var packages = _ai.ReconstructPackages();
        var resolvedCount = SpawnPositionResolver.ResolveSpawnPositions(cells, packages, npcs, creatures);
        var mapMarkers = _world.ExtractMapMarkers();
        var leveledLists = _misc.ReconstructLeveledLists();
        Logger.Instance.Debug(
            $"  [Semantic] World: {phaseSw.Elapsed} (Cells: {cells.Count} in {cellTime}, Worldspaces: {worldspaces.Count}, Packages: {packages.Count}, SpawnResolved: {resolvedCount}, MapMarkers: {mapMarkers.Count}, LeveledLists: {leveledLists.Count})");

        progress?.Report((80, "Reconstructing game data..."));
        phaseSw.Restart();
        var gameSettings = _misc.ReconstructGameSettings();
        var globals = _misc.ReconstructGlobals();
        var enchantments = _effects.ReconstructEnchantments();
        var baseEffects = _effects.ReconstructBaseEffects();
        var weaponMods = _misc.ReconstructWeaponMods();
        var recipes = _misc.ReconstructRecipes();
        var challenges = _misc.ReconstructChallenges();
        var reputations = _misc.ReconstructReputations();
        var projectiles = _effects.ReconstructProjectiles();
        var explosions = _effects.ReconstructExplosions();
        var messages = _text.ReconstructMessages();
        var classes = _misc.ReconstructClasses();
        var formLists = _misc.ReconstructFormLists();
        var activators = _misc.ReconstructActivators();
        var lights = _misc.ReconstructLights();
        var doors = _misc.ReconstructDoors();
        var statics = _misc.ReconstructStatics();
        var furniture = _misc.ReconstructFurniture();
        Logger.Instance.Debug($"  [Semantic] Game data: {phaseSw.Elapsed} (16 types)");

        progress?.Report((95, "Building lookup tables..."));

        var result = new RecordCollection
        {
            // Characters
            Npcs = npcs,
            Creatures = creatures,
            Races = races,
            Factions = factions,

            // Quests and Dialogue
            Quests = quests,
            DialogTopics = dialogTopics,
            Dialogues = dialogues,
            DialogueTree = dialogueTree,
            Notes = notes,
            Books = books,
            Terminals = terminals,
            Scripts = scripts,

            // Items
            Weapons = weapons,
            Armor = armor,
            Ammo = ammo,
            Consumables = consumables,
            MiscItems = miscItems,
            Keys = keys,
            Containers = containers,

            // Abilities
            Perks = perks,
            Spells = spells,

            // World
            Cells = cells,
            Worldspaces = worldspaces,
            MapMarkers = mapMarkers,
            LeveledLists = leveledLists,

            // Game Data
            GameSettings = gameSettings,
            Globals = globals,
            Enchantments = enchantments,
            BaseEffects = baseEffects,
            WeaponMods = weaponMods,
            Recipes = recipes,
            Challenges = challenges,
            Reputations = reputations,
            Projectiles = projectiles,
            Explosions = explosions,
            Messages = messages,
            Classes = classes,
            FormLists = formLists,
            Activators = activators,
            Lights = lights,
            Doors = doors,
            Statics = statics,
            Furniture = furniture,

            // AI
            Packages = packages,

            FormIdToEditorId = new Dictionary<uint, string>(_context.FormIdToEditorId),
            FormIdToDisplayName = _context.BuildFormIdToDisplayNameMap(),
            TotalRecordsProcessed = _context.ScanResult.MainRecords.Count,
            UnreconstructedTypeCounts = unreconstructedCounts
        };

        totalSw.Stop();
        Logger.Instance.Info(
            $"[Semantic Reconstruction] Complete. Time: {totalSw.Elapsed}, Records: {result.TotalRecordsReconstructed}");

        progress?.Report((100, "Complete"));
        return result;
    }

    #endregion

    #region Public API - Lookup Methods

    public string? GetEditorId(uint formId) => _context.GetEditorId(formId);
    public uint? GetFormId(string editorId) => _context.GetFormId(editorId);
    public DetectedMainRecord? GetRecord(uint formId) => _context.GetRecord(formId);
    public IEnumerable<DetectedMainRecord> GetRecordsByType(string recordType) => _context.GetRecordsByType(recordType);

    #endregion

    #region Public API - Individual Reconstruction (for direct caller access)

    // Actors
    public List<NpcRecord> ReconstructNpcs() => _actors.ReconstructNpcs();
    public List<CreatureRecord> ReconstructCreatures() => _actors.ReconstructCreatures();
    public List<FactionRecord> ReconstructFactions() => _actors.ReconstructFactions();
    public List<RaceRecord> ReconstructRaces() => _actors.ReconstructRaces();

    // Items
    public List<WeaponRecord> ReconstructWeapons() => _items.ReconstructWeapons();
    public List<ArmorRecord> ReconstructArmor() => _items.ReconstructArmor();
    public List<AmmoRecord> ReconstructAmmo() => _items.ReconstructAmmo();
    public List<ConsumableRecord> ReconstructConsumables() => _items.ReconstructConsumables();
    public List<MiscItemRecord> ReconstructMiscItems() => _items.ReconstructMiscItems();
    public List<KeyRecord> ReconstructKeys() => _items.ReconstructKeys();
    public List<ContainerRecord> ReconstructContainers() => _items.ReconstructContainers();

    // Dialogue
    public List<QuestRecord> ReconstructQuests() => _dialogue.ReconstructQuests();
    public List<DialogTopicRecord> ReconstructDialogTopics() => _dialogue.ReconstructDialogTopics();
    public List<DialogueRecord> ReconstructDialogue() => _dialogue.ReconstructDialogue();
    public DialogueTreeResult BuildDialogueTrees(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics,
        List<QuestRecord> quests) => _dialogue.BuildDialogueTrees(dialogues, topics, quests);

    // Text
    public List<NoteRecord> ReconstructNotes() => _text.ReconstructNotes();
    public List<BookRecord> ReconstructBooks() => _text.ReconstructBooks();
    public List<TerminalRecord> ReconstructTerminals() => _text.ReconstructTerminals();
    public List<MessageRecord> ReconstructMessages() => _text.ReconstructMessages();

    // Scripts
    public List<ScriptRecord> ReconstructScripts() => _scripts.ReconstructScripts();

    // Effects
    public List<PerkRecord> ReconstructPerks() => _effects.ReconstructPerks();
    public List<SpellRecord> ReconstructSpells() => _effects.ReconstructSpells();
    public List<EnchantmentRecord> ReconstructEnchantments() => _effects.ReconstructEnchantments();
    public List<BaseEffectRecord> ReconstructBaseEffects() => _effects.ReconstructBaseEffects();
    public List<ProjectileRecord> ReconstructProjectiles() => _effects.ReconstructProjectiles();
    public List<ExplosionRecord> ReconstructExplosions() => _effects.ReconstructExplosions();

    // World
    public List<CellRecord> ReconstructCells() => _world.ReconstructCells();
    public List<WorldspaceRecord> ReconstructWorldspaces() => _world.ReconstructWorldspaces();
    public List<PlacedReference> ExtractMapMarkers() => _world.ExtractMapMarkers();

    // Misc
    public List<GameSettingRecord> ReconstructGameSettings() => _misc.ReconstructGameSettings();
    public List<GlobalRecord> ReconstructGlobals() => _misc.ReconstructGlobals();
    public List<WeaponModRecord> ReconstructWeaponMods() => _misc.ReconstructWeaponMods();
    public List<RecipeRecord> ReconstructRecipes() => _misc.ReconstructRecipes();
    public List<ChallengeRecord> ReconstructChallenges() => _misc.ReconstructChallenges();
    public List<ReputationRecord> ReconstructReputations() => _misc.ReconstructReputations();
    public List<ClassRecord> ReconstructClasses() => _misc.ReconstructClasses();
    public List<LeveledListRecord> ReconstructLeveledLists() => _misc.ReconstructLeveledLists();
    public List<FormListRecord> ReconstructFormLists() => _misc.ReconstructFormLists();
    public List<ActivatorRecord> ReconstructActivators() => _misc.ReconstructActivators();
    public List<LightRecord> ReconstructLights() => _misc.ReconstructLights();
    public List<DoorRecord> ReconstructDoors() => _misc.ReconstructDoors();
    public List<StaticRecord> ReconstructStatics() => _misc.ReconstructStatics();
    public List<FurnitureRecord> ReconstructFurniture() => _misc.ReconstructFurniture();

    // AI
    public List<PackageRecord> ReconstructPackages() => _ai.ReconstructPackages();

    #endregion
}

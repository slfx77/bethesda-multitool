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
            "PACK",
            // Generic record types (Phase 1)
            "MSTT", "TACT", "CAMS", "ANIO", "IPDS", "EFSH", "RGDL", "LSCR",
            "ASPC", "MSET", "CHIP", "CSNO", "DOBJ", "ADDN", "TREE", "IMAD",
            // Specialized record types (Phase 2)
            "SOUN", "TXST", "ARMA", "WATR", "BPTD", "AVIF", "CSTY", "LGTM", "NAVM", "WTHR"
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
            // Use pAllForms entries (LAND records lack editor IDs, so they're absent from RuntimeEditorIds)
            var landEntries = _context.ScanResult.RuntimeLandFormEntries.Count > 0
                ? _context.ScanResult.RuntimeLandFormEntries
                : _context.ScanResult.RuntimeEditorIds; // Fallback for compatibility
            var runtimeLandData = _context.RuntimeReader.ReadAllRuntimeLandData(landEntries);
            if (runtimeLandData.Count > 0)
            {
                var existingCount = _context.ScanResult.LandRecords.Count;
                EsmWorldExtractor.EnrichLandRecordsWithRuntimeData(_context.ScanResult, runtimeLandData);
                var addedCount = _context.ScanResult.LandRecords.Count - existingCount;
                Logger.Instance.Debug(
                    $"  [Semantic] Enriched LAND records: {runtimeLandData.Count} with terrain data " +
                    $"({existingCount} existing + {addedCount} runtime-only = {_context.ScanResult.LandRecords.Count} total)");
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

        // Backfill: quests discovered from dialogue QSTI references but not from QUST records.
        // For some DMPs, runtime quest detection finds nothing, but dialogue INFOs still reference
        // quest FormIDs via QSTI subrecords. Create stub QuestRecords so the Data Browser shows them.
        var existingQuestIds = new HashSet<uint>(quests.Select(q => q.FormId));
        foreach (var (questFormId, questNode) in dialogueTree.QuestTrees)
        {
            if (questFormId != 0 && !existingQuestIds.Contains(questFormId))
            {
                quests.Add(new QuestRecord
                {
                    FormId = questFormId,
                    EditorId = _context.GetEditorId(questFormId),
                    FullName = _context.FormIdToFullName.GetValueOrDefault(questFormId)
                               ?? questNode.QuestName,
                    Offset = 0,
                    IsBigEndian = true
                });
            }
        }

        var notes = _text.ReconstructNotes();
        var books = _text.ReconstructBooks();
        var terminals = _text.ReconstructTerminals();

        // Reconstruct ACTI/DOOR/FURN early so their Script FormIDs are available
        // for cross-reference chain building below.
        var activators = _misc.ReconstructActivators();
        var doors = _misc.ReconstructDoors();
        var furniture = _misc.ReconstructFurniture();

        // Build runtime object→script mappings for DMP cross-reference chains.
        // In memory dumps, ESM records are freed at load time so the ESM-based
        // BuildCrossReferenceChains finds nothing. Runtime struct readers extract
        // Script FormIDs from C++ object pointers (NPC_, CREA, CONT, ACTI, DOOR, FURN) instead.
        if (_context.RuntimeReader != null)
        {
            var runtimeObjectToScript = new Dictionary<uint, uint>();
            foreach (var npc in npcs.Where(n => n.Script is > 0))
            {
                runtimeObjectToScript.TryAdd(npc.FormId, npc.Script!.Value);
            }

            foreach (var creature in creatures.Where(c => c.Script is > 0))
            {
                runtimeObjectToScript.TryAdd(creature.FormId, creature.Script!.Value);
            }

            foreach (var container in containers.Where(c => c.Script is > 0))
            {
                runtimeObjectToScript.TryAdd(container.FormId, container.Script!.Value);
            }

            foreach (var activator in activators.Where(a => a.Script is > 0))
            {
                runtimeObjectToScript.TryAdd(activator.FormId, activator.Script!.Value);
            }

            foreach (var door in doors.Where(d => d.Script is > 0))
            {
                runtimeObjectToScript.TryAdd(door.FormId, door.Script!.Value);
            }

            foreach (var furn in furniture.Where(f => f.Script is > 0))
            {
                runtimeObjectToScript.TryAdd(furn.FormId, furn.Script!.Value);
            }

            if (runtimeObjectToScript.Count > 0)
            {
                _scripts.SetRuntimeObjectScriptMappings(runtimeObjectToScript);
                Logger.Instance.Debug(
                    $"  [Semantic] Runtime obj→script: {runtimeObjectToScript.Count} mappings " +
                    $"(NPCs: {npcs.Count(n => n.Script is > 0)}, " +
                    $"Creatures: {creatures.Count(c => c.Script is > 0)}, " +
                    $"Containers: {containers.Count(c => c.Script is > 0)}, " +
                    $"Activators: {activators.Count(a => a.Script is > 0)}, " +
                    $"Doors: {doors.Count(d => d.Script is > 0)}, " +
                    $"Furniture: {furniture.Count(f => f.Script is > 0)})");
            }
        }

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

        // DMP fallback: infer worldspace membership from cell grid coordinates when GRUP data is absent
        if (_context.ScanResult.CellToWorldspaceMap.Count == 0 && worldspaces.Count > 0)
        {
            WorldRecordHandler.InferCellWorldspaces(cells, worldspaces);
        }

        // DMP fallback: create virtual cells for orphan REFR/ACHR/ACRE not assigned to any cell
        if (_context.ScanResult.CellToRefrMap.Count == 0 && _context.ScanResult.RefrRecords.Count > 0)
        {
            var virtualCells = WorldRecordHandler.CreateVirtualCells(
                cells, _context.ScanResult.RefrRecords, _context);
            cells.AddRange(virtualCells);
        }

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
        // activators, doors, furniture already reconstructed above (before script cross-ref chains)
        var lights = _misc.ReconstructLights();
        var statics = _misc.ReconstructStatics();
        Logger.Instance.Debug($"  [Semantic] Game data: {phaseSw.Elapsed} (16 types)");

        progress?.Report((85, "Reconstructing generic records..."));
        phaseSw.Restart();
        var genericTypes = new[]
        {
            "MSTT", "TACT", "CAMS", "ANIO", "IPDS", "EFSH", "RGDL", "LSCR",
            "ASPC", "MSET", "CHIP", "CSNO", "DOBJ", "ADDN", "TREE", "IMAD"
        };
        var genericRecords = new List<GenericEsmRecord>();
        foreach (var type in genericTypes)
        {
            genericRecords.AddRange(_misc.ReconstructGenericRecords(type));
        }

        Logger.Instance.Debug(
            $"  [Semantic] Generic records: {phaseSw.Elapsed} ({genericRecords.Count} across {genericTypes.Length} types)");

        progress?.Report((88, "Reconstructing specialized records..."));
        phaseSw.Restart();
        var sounds = _misc.ReconstructSounds();
        var textureSets = _misc.ReconstructTextureSets();
        var armorAddons = _misc.ReconstructArmorAddons();
        var water = _misc.ReconstructWater();
        var bodyPartData = _misc.ReconstructBodyPartData();
        var actorValueInfos = _misc.ReconstructActorValueInfos();
        var combatStyles = _misc.ReconstructCombatStyles();
        var lightingTemplates = _misc.ReconstructLightingTemplates();
        var navMeshes = _misc.ReconstructNavMeshes();
        var weather = _misc.ReconstructWeather();
        Logger.Instance.Debug(
            $"  [Semantic] Specialized records: {phaseSw.Elapsed} " +
            $"(SOUN: {sounds.Count}, TXST: {textureSets.Count}, ARMA: {armorAddons.Count}, " +
            $"WATR: {water.Count}, BPTD: {bodyPartData.Count}, AVIF: {actorValueInfos.Count}, " +
            $"CSTY: {combatStyles.Count}, LGTM: {lightingTemplates.Count}, " +
            $"NAVM: {navMeshes.Count}, WTHR: {weather.Count})");

        // Enrich placed references with base object bounds and model paths
        phaseSw.Restart();
        var boundsIndex = new Dictionary<uint, ObjectBounds>();
        var modelIndex = new Dictionary<uint, string>();
        AddToIndexes(statics, s => s.FormId, s => s.Bounds, s => s.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(activators, a => a.FormId, a => a.Bounds, a => a.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(doors, d => d.FormId, d => d.Bounds, d => d.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(lights, l => l.FormId, l => l.Bounds, l => l.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(furniture, f => f.FormId, f => f.Bounds, f => f.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(weapons, w => w.FormId, w => w.Bounds, w => w.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(armor, a => a.FormId, a => a.Bounds, a => a.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(ammo, a => a.FormId, a => a.Bounds, a => a.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(consumables, c => c.FormId, c => c.Bounds, c => c.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(miscItems, m => m.FormId, m => m.Bounds, m => m.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(books, b => b.FormId, b => b.Bounds, b => b.ModelPath, boundsIndex, modelIndex);
        AddToIndexes(containers, c => c.FormId, c => (ObjectBounds?)null, c => c.ModelPath, boundsIndex, modelIndex);

        WorldRecordHandler.EnrichPlacedReferences(cells, boundsIndex, modelIndex);
        foreach (var ws in worldspaces)
        {
            WorldRecordHandler.EnrichPlacedReferences(ws.Cells, boundsIndex, modelIndex);
        }

        Logger.Instance.Debug(
            $"  [Semantic] Enrichment: {phaseSw.Elapsed} (Bounds: {boundsIndex.Count}, Models: {modelIndex.Count})");

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

            // Generic
            GenericRecords = genericRecords,

            // Specialized Phase 2
            Sounds = sounds,
            TextureSets = textureSets,
            ArmorAddons = armorAddons,
            Water = water,
            BodyPartData = bodyPartData,
            ActorValueInfos = actorValueInfos,
            CombatStyles = combatStyles,
            LightingTemplates = lightingTemplates,
            NavMeshes = navMeshes,
            Weather = weather,

            ModelPathIndex = modelIndex,
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

    #region Private Helpers

    private static void AddToIndexes<T>(
        List<T> records,
        Func<T, uint> formIdSelector,
        Func<T, ObjectBounds?> boundsSelector,
        Func<T, string?> modelSelector,
        Dictionary<uint, ObjectBounds> boundsIndex,
        Dictionary<uint, string> modelIndex)
    {
        foreach (var record in records)
        {
            var formId = formIdSelector(record);
            if (formId == 0)
            {
                continue;
            }

            var bounds = boundsSelector(record);
            if (bounds != null)
            {
                boundsIndex.TryAdd(formId, bounds);
            }

            var model = modelSelector(record);
            if (model != null)
            {
                modelIndex.TryAdd(formId, model);
            }
        }
    }

    #endregion
}

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reconstructs semantic game data from ESM record scan results and runtime memory structures.
///     Facade that delegates to domain-specific handler classes.
/// </summary>
public sealed class RecordParser
{
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
        _weapons = new WeaponRecordHandler(_context);
        _consumables = new ConsumableRecordHandler(_context);
        _dialogue = new DialogueRecordHandler(_context);
        _text = new TextRecordHandler(_context);
        _scripts = new ScriptRecordHandler(_context);
        _effects = new EffectRecordHandler(_context);
        _combatEffects = new CombatEffectHandler(_context);
        _world = new WorldRecordHandler(_context);
        _misc = new MiscRecordHandler(_context);
        _miscBasicTypes = new MiscBasicTypeHandler(_context);
        _miscItems = new MiscItemHandler(_context);
        _miscWorldObjects = new MiscWorldObjectHandler(_context);
        _miscStaticObjects = new MiscStaticObjectHandler(_context);
        _miscEnvironment = new MiscEnvironmentHandler(_context);
        _miscGameSystems = new MiscGameSystemHandler(_context);
        _miscCollections = new MiscCollectionHandler(_context);
        _ai = new AiRecordHandler(_context);
    }

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
            "IDLM", "SCOL", "PWAT",
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

        // === Runtime enrichment phases (LAND, REFR, worldspace cell maps) ===
        RuntimeDataEnricher.EnrichLandRecords(_context);
        RuntimeDataEnricher.EnrichPlacedReferences(_context, phaseSw);
        RuntimeDataEnricher.EnrichWorldspaceCellMaps(_context, phaseSw);

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
        var weapons = _weapons.ReconstructWeapons();
        var ammo = _consumables.ReconstructAmmo();
        _consumables.EnrichAmmoWithProjectileModels(weapons, ammo);
        _weapons.EnrichWeaponsWithProjectileData(weapons);
        var armor = _items.ReconstructArmor();
        var consumables = _consumables.ReconstructConsumables();
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
        var activators = _miscWorldObjects.ReconstructActivators();
        var doors = _miscWorldObjects.ReconstructDoors();
        var furniture = _miscStaticObjects.ReconstructFurniture();

        // === Runtime script cross-reference chains ===
        QuestScriptEnricher.BuildRuntimeScriptMappings(
            _context, _scripts, npcs, creatures, containers, activators, doors, furniture);

        var scripts = _scripts.ReconstructScripts();
        Logger.Instance.Debug(
            $"  [Semantic] Trees/text: {phaseSw.Elapsed} (Notes: {notes.Count}, Books: {books.Count}, Terminals: {terminals.Count}, Scripts: {scripts.Count})");

        // === Quest enrichment: PathwayD backfill, variables cross-reference, related NPCs ===
        QuestScriptEnricher.EnrichQuests(_context, quests, scripts, dialogues, phaseSw);

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
        var leveledLists = _miscCollections.ReconstructLeveledLists();
        var resolvedCount =
            SpawnPositionResolver.ResolveSpawnPositions(cells, packages, npcs, creatures, leveledLists);
        var mapMarkers = _world.ExtractMapMarkers();
        Logger.Instance.Debug(
            $"  [Semantic] World: {phaseSw.Elapsed} (Cells: {cells.Count} in {cellTime}, Worldspaces: {worldspaces.Count}, Packages: {packages.Count}, SpawnResolved: {resolvedCount}, MapMarkers: {mapMarkers.Count}, LeveledLists: {leveledLists.Count})");

        progress?.Report((80, "Reconstructing game data..."));
        phaseSw.Restart();
        var gameSettings = _misc.ReconstructGameSettings();
        var globals = _miscBasicTypes.ReconstructGlobals();
        var enchantments = _effects.ReconstructEnchantments();
        var baseEffects = _effects.ReconstructBaseEffects();
        var weaponMods = _miscItems.ReconstructWeaponMods();
        var recipes = _miscItems.ReconstructRecipes();
        var challenges = _miscBasicTypes.ReconstructChallenges();
        var reputations = _miscBasicTypes.ReconstructReputations();
        var projectiles = _combatEffects.ReconstructProjectiles();
        var explosions = _combatEffects.ReconstructExplosions();
        var messages = _text.ReconstructMessages();
        var classes = _miscBasicTypes.ReconstructClasses();
        var formLists = _miscCollections.ReconstructFormLists();
        // activators, doors, furniture already reconstructed above (before script cross-ref chains)
        var lights = _miscWorldObjects.ReconstructLights();
        var statics = _miscStaticObjects.ReconstructStatics();
        Logger.Instance.Debug($"  [Semantic] Game data: {phaseSw.Elapsed} (16 types)");

        progress?.Report((85, "Reconstructing generic records..."));
        phaseSw.Restart();
        var genericTypes = new[]
        {
            "MSTT", "TACT", "CAMS", "ANIO", "IPDS", "EFSH", "RGDL", "LSCR",
            "ASPC", "MSET", "CHIP", "CSNO", "DOBJ", "ADDN", "TREE", "IMAD",
            "IDLM", "SCOL", "PWAT"
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
        var sounds = _miscEnvironment.ReconstructSounds();
        var textureSets = _miscEnvironment.ReconstructTextureSets();
        var armorAddons = _miscItems.ReconstructArmorAddons();
        var water = _miscEnvironment.ReconstructWater();
        var bodyPartData = _miscItems.ReconstructBodyPartData();
        var actorValueInfos = _miscGameSystems.ReconstructActorValueInfos();
        var combatStyles = _miscGameSystems.ReconstructCombatStyles();
        var lightingTemplates = _miscGameSystems.ReconstructLightingTemplates();
        var navMeshes = _miscGameSystems.ReconstructNavMeshes();
        var weather = _miscEnvironment.ReconstructWeather();
        Logger.Instance.Debug(
            $"  [Semantic] Specialized records: {phaseSw.Elapsed} " +
            $"(SOUN: {sounds.Count}, TXST: {textureSets.Count}, ARMA: {armorAddons.Count}, " +
            $"WATR: {water.Count}, BPTD: {bodyPartData.Count}, AVIF: {actorValueInfos.Count}, " +
            $"CSTY: {combatStyles.Count}, LGTM: {lightingTemplates.Count}, " +
            $"NAVM: {navMeshes.Count}, WTHR: {weather.Count})");

        // === Build object bounds/model indexes and enrich placed references ===
        var modelIndex = new Dictionary<uint, string>();
        ObjectIndexBuilder.BuildAndEnrich(
            statics, activators, doors, lights, furniture,
            weapons, armor, ammo, consumables, miscItems, books,
            containers, keys, notes, weaponMods, sounds, genericRecords,
            cells, worldspaces, modelIndex, phaseSw);

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

    internal readonly RecordParserContext _context;

    // Domain-specific handlers
    private readonly ActorRecordHandler _actors;
    private readonly ItemRecordHandler _items;
    private readonly WeaponRecordHandler _weapons;
    private readonly ConsumableRecordHandler _consumables;
    private readonly DialogueRecordHandler _dialogue;
    private readonly TextRecordHandler _text;
    private readonly ScriptRecordHandler _scripts;
    private readonly EffectRecordHandler _effects;
    private readonly CombatEffectHandler _combatEffects;
    private readonly WorldRecordHandler _world;
    private readonly MiscRecordHandler _misc;
    private readonly MiscBasicTypeHandler _miscBasicTypes;
    private readonly MiscItemHandler _miscItems;
    private readonly MiscWorldObjectHandler _miscWorldObjects;
    private readonly MiscStaticObjectHandler _miscStaticObjects;
    private readonly MiscEnvironmentHandler _miscEnvironment;
    private readonly MiscGameSystemHandler _miscGameSystems;
    private readonly MiscCollectionHandler _miscCollections;
    private readonly AiRecordHandler _ai;

    public string? GetEditorId(uint formId)
    {
        return _context.GetEditorId(formId);
    }

    public uint? GetFormId(string editorId)
    {
        return _context.GetFormId(editorId);
    }

    public DetectedMainRecord? GetRecord(uint formId)
    {
        return _context.GetRecord(formId);
    }

    public IEnumerable<DetectedMainRecord> GetRecordsByType(string recordType)
    {
        return _context.GetRecordsByType(recordType);
    }

    // Actors
    public List<NpcRecord> ReconstructNpcs() => _actors.ReconstructNpcs();
    public List<CreatureRecord> ReconstructCreatures() => _actors.ReconstructCreatures();
    public List<FactionRecord> ReconstructFactions() => _actors.ReconstructFactions();
    public List<RaceRecord> ReconstructRaces() => _actors.ReconstructRaces();

    // Items
    public List<WeaponRecord> ReconstructWeapons() => _weapons.ReconstructWeapons();
    public List<ArmorRecord> ReconstructArmor() => _items.ReconstructArmor();
    public List<AmmoRecord> ReconstructAmmo() => _consumables.ReconstructAmmo();
    public List<ConsumableRecord> ReconstructConsumables() => _consumables.ReconstructConsumables();
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
    public List<ProjectileRecord> ReconstructProjectiles() => _combatEffects.ReconstructProjectiles();
    public List<ExplosionRecord> ReconstructExplosions() => _combatEffects.ReconstructExplosions();

    // World
    public List<CellRecord> ReconstructCells() => _world.ReconstructCells();
    public List<WorldspaceRecord> ReconstructWorldspaces() => _world.ReconstructWorldspaces();
    public List<PlacedReference> ExtractMapMarkers() => _world.ExtractMapMarkers();

    // Misc
    public List<GameSettingRecord> ReconstructGameSettings() => _misc.ReconstructGameSettings();
    public List<GlobalRecord> ReconstructGlobals() => _miscBasicTypes.ReconstructGlobals();
    public List<WeaponModRecord> ReconstructWeaponMods() => _miscItems.ReconstructWeaponMods();
    public List<RecipeRecord> ReconstructRecipes() => _miscItems.ReconstructRecipes();
    public List<ChallengeRecord> ReconstructChallenges() => _miscBasicTypes.ReconstructChallenges();
    public List<ReputationRecord> ReconstructReputations() => _miscBasicTypes.ReconstructReputations();
    public List<ClassRecord> ReconstructClasses() => _miscBasicTypes.ReconstructClasses();
    public List<LeveledListRecord> ReconstructLeveledLists() => _miscCollections.ReconstructLeveledLists();
    public List<FormListRecord> ReconstructFormLists() => _miscCollections.ReconstructFormLists();
    public List<ActivatorRecord> ReconstructActivators() => _miscWorldObjects.ReconstructActivators();
    public List<LightRecord> ReconstructLights() => _miscWorldObjects.ReconstructLights();
    public List<DoorRecord> ReconstructDoors() => _miscWorldObjects.ReconstructDoors();
    public List<StaticRecord> ReconstructStatics() => _miscStaticObjects.ReconstructStatics();
    public List<FurnitureRecord> ReconstructFurniture() => _miscStaticObjects.ReconstructFurniture();

    // AI
    public List<PackageRecord> ReconstructPackages() => _ai.ReconstructPackages();
}

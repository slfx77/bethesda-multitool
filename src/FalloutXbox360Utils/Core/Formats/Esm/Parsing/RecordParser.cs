using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Parses semantic game data from ESM record scan results and runtime memory structures.
///     Facade that delegates to domain-specific handler classes.
/// </summary>
public sealed class RecordParser
{
    // Domain-specific handlers
    private readonly ActorRecordHandler _actors;
    private readonly AiRecordHandler _ai;
    private readonly CombatEffectHandler _combatEffects;
    private readonly ConsumableRecordHandler _consumables;

    internal readonly RecordParserContext _context;
    private readonly DialogueRecordHandler _dialogue;
    private readonly EffectRecordHandler _effects;
    private readonly ItemRecordHandler _items;
    private readonly MiscRecordHandler _misc;
    private readonly MiscBasicTypeHandler _miscBasicTypes;
    private readonly MiscCollectionHandler _miscCollections;
    private readonly MiscEnvironmentHandler _miscEnvironment;
    private readonly MiscGameSystemHandler _miscGameSystems;
    private readonly MiscItemHandler _miscItems;
    private readonly MiscStaticObjectHandler _miscStaticObjects;
    private readonly MiscWorldObjectHandler _miscWorldObjects;
    private readonly ScriptRecordHandler _scripts;
    private readonly TextRecordHandler _text;
    private readonly WeaponRecordHandler _weapons;
    private readonly WorldRecordHandler _world;

    public RecordParser(
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? formIdCorrelations = null,
        MemoryMappedViewAccessor? accessor = null,
        long fileSize = 0,
        MinidumpInfo? minidumpInfo = null)
        : this(new RecordParserContext(scanResult, formIdCorrelations, accessor, fileSize, minidumpInfo))
    {
    }

    public RecordParser(
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? formIdCorrelations,
        IMemoryAccessor? accessor,
        long fileSize,
        MinidumpInfo? minidumpInfo)
        : this(new RecordParserContext(scanResult, formIdCorrelations, accessor, fileSize, minidumpInfo))
    {
    }

    internal RecordParser(RecordParserContext context)
    {
        _context = context;

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
    ///     Perform full semantic parse of all supported record types.
    /// </summary>
    public RecordCollection ParseAll(IProgress<(int percent, string phase)>? progress = null)
    {
        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

        // Parsed record types
        var parsedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
            "SOUN", "MUSC", "TXST", "ARMA", "WATR", "BPTD", "AVIF", "CSTY", "LGTM", "NAVM", "WTHR"
        };

        // Count all record types and compute unparsed counts
        var allTypeCounts = _context.ScanResult.MainRecords
            .GroupBy(r => r.RecordType)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var unparsedCounts = allTypeCounts
            .Where(kvp => !parsedTypes.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // === Runtime enrichment phases (LAND, REFR, worldspace cell maps) ===
        RuntimeDataEnricher.EnrichLandRecords(_context);
        RuntimeDataEnricher.EnrichPlacedReferences(_context, phaseSw);
        RuntimeDataEnricher.EnrichWorldspaceCellMaps(_context, phaseSw);

        // Pre-scan all records for FULL subrecords (display names) so that
        // unparsed types (HAIR, EYES, CSTY, etc.) have names available for display
        progress?.Report((2, "Scanning display names..."));
        phaseSw.Restart();
        _context.CaptureAllFullNames();
        Logger.Instance.Debug(
            $"  [Semantic] Display names: {phaseSw.Elapsed} ({_context.FormIdToFullName.Count} names captured)");

        // Build weapons and ammo first, then cross-reference for projectile data
        progress?.Report((5, "Parseing characters..."));
        phaseSw.Restart();
        var npcs = _actors.ParseNpcs();
        var creatures = _actors.ParseCreatures();
        var races = _actors.ParseRaces();
        var factions = _actors.ParseFactions();
        Logger.Instance.Debug(
            $"  [Semantic] Characters: {phaseSw.Elapsed} (NPCs: {npcs.Count}, Creatures: {creatures.Count}, Races: {races.Count}, Factions: {factions.Count})");

        progress?.Report((15, "Parseing items..."));
        phaseSw.Restart();
        var weapons = _weapons.ParseWeapons();
        var ammo = _consumables.ParseAmmo();
        _consumables.EnrichAmmoWithProjectileModels(weapons, ammo);
        _weapons.EnrichWeaponsWithProjectileData(weapons);
        _weapons.EnrichWeaponsWithEsmProjectileData(weapons);
        var armor = _items.ParseArmor();
        var consumables = _consumables.ParseConsumables();
        var miscItems = _items.ParseMiscItems();
        var keys = _items.ParseKeys();
        var containers = _items.ParseContainers();
        Logger.Instance.Debug(
            $"  [Semantic] Items: {phaseSw.Elapsed} (Weapons: {weapons.Count}, Armor: {armor.Count}, Ammo: {ammo.Count}, Consumables: {consumables.Count}, Misc: {miscItems.Count}, Keys: {keys.Count}, Containers: {containers.Count})");

        // Build dialogue data, then construct the tree hierarchy
        progress?.Report((30, "Parseing dialogue..."));
        phaseSw.Restart();
        var quests = _dialogue.ParseQuests();
        var dialogTopics = _dialogue.ParseDialogTopics();
        var dialogues = _dialogue.ParseDialogue();

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

        var notes = _text.ParseNotes();
        var books = _text.ParseBooks();
        var terminals = _text.ParseTerminals();

        // Parse ACTI/DOOR/FURN early so their Script FormIDs are available
        // for cross-reference chain building below.
        var activators = _miscWorldObjects.ParseActivators();
        var doors = _miscWorldObjects.ParseDoors();
        var furniture = _miscStaticObjects.ParseFurniture();

        // === Runtime script cross-reference chains ===
        QuestScriptEnricher.BuildRuntimeScriptMappings(
            _context, _scripts, npcs, creatures, containers, activators, doors, furniture);

        var scripts = _scripts.ParseScripts();
        Logger.Instance.Debug(
            $"  [Semantic] Trees/text: {phaseSw.Elapsed} (Notes: {notes.Count}, Books: {books.Count}, Terminals: {terminals.Count}, Scripts: {scripts.Count})");

        // === Quest enrichment: PathwayD backfill, variables cross-reference, related NPCs ===
        QuestScriptEnricher.EnrichQuests(_context, quests, scripts, dialogues, phaseSw);

        progress?.Report((55, "Parseing abilities..."));
        phaseSw.Restart();
        var perks = _effects.ParsePerks();
        var spells = _effects.ParseSpells();
        Logger.Instance.Debug(
            $"  [Semantic] Abilities: {phaseSw.Elapsed} (Perks: {perks.Count}, Spells: {spells.Count})");

        progress?.Report((60, "Parseing world data..."));
        phaseSw.Restart();
        var cells = _world.ParseCells();
        var cellTime = phaseSw.Elapsed;
        var worldspaces = _world.ParseWorldspaces();

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

            // New runtime-only stubs can pick up worldspace membership after orphan-cell recovery.
            if (_context.ScanResult.CellToWorldspaceMap.Count == 0 && worldspaces.Count > 0)
            {
                WorldRecordHandler.InferCellWorldspaces(cells, worldspaces);
            }
        }

        WorldRecordHandler.EnsureWorldspacesForCells(cells, worldspaces, _context);
        WorldRecordHandler.LinkCellsToWorldspaces(cells, worldspaces);
        var packages = _ai.ParsePackages();
        var leveledLists = _miscCollections.ParseLeveledLists();
        var resolvedCount =
            SpawnPositionResolver.ResolveSpawnPositions(cells, packages, npcs, creatures, leveledLists);
        var mapMarkers = _world.ExtractMapMarkers();
        Logger.Instance.Debug(
            $"  [Semantic] World: {phaseSw.Elapsed} (Cells: {cells.Count} in {cellTime}, Worldspaces: {worldspaces.Count}, Packages: {packages.Count}, SpawnResolved: {resolvedCount}, MapMarkers: {mapMarkers.Count}, LeveledLists: {leveledLists.Count})");

        progress?.Report((80, "Parseing game data..."));
        phaseSw.Restart();
        var gameSettings = _misc.ParseGameSettings();
        var globals = _miscBasicTypes.ParseGlobals();
        var enchantments = _effects.ParseEnchantments();
        var baseEffects = _effects.ParseBaseEffects();
        var weaponMods = _miscItems.ParseWeaponMods();
        var recipes = _miscItems.ParseRecipes();
        var challenges = _miscBasicTypes.ParseChallenges();
        var reputations = _miscBasicTypes.ParseReputations();
        var projectiles = _combatEffects.ParseProjectiles();
        _combatEffects.EnrichProjectilesWithRuntime(projectiles);
        var explosions = _combatEffects.ParseExplosions();
        var messages = _text.ParseMessages();
        var classes = _miscBasicTypes.ParseClasses();
        var eyes = _miscBasicTypes.ParseEyes();
        var hair = _miscBasicTypes.ParseHair();
        var formLists = _miscCollections.ParseFormLists();
        // activators, doors, furniture already parsed above (before script cross-ref chains)
        var lights = _miscWorldObjects.ParseLights();
        var statics = _miscStaticObjects.ParseStatics();
        Logger.Instance.Debug($"  [Semantic] Game data: {phaseSw.Elapsed} (16 types)");

        progress?.Report((85, "Parseing generic records..."));
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
            genericRecords.AddRange(_misc.ParseGenericRecords(type));
        }

        Logger.Instance.Debug(
            $"  [Semantic] Generic records: {phaseSw.Elapsed} ({genericRecords.Count} across {genericTypes.Length} types)");

        // Merge runtime-only records using PDB-derived struct layouts for types without specialized readers
        phaseSw.Restart();
        var allEsmFormIds = new HashSet<uint>(_context.RecordsByFormId.Keys);
        foreach (var gr in genericRecords)
        {
            allEsmFormIds.Add(gr.FormId);
        }

        _context.MergeRuntimeGenericRecords(genericRecords, allEsmFormIds);
        Logger.Instance.Debug($"  [Semantic] PDB generic runtime merge: {phaseSw.Elapsed}");

        progress?.Report((88, "Parseing specialized records..."));
        phaseSw.Restart();
        var sounds = _miscEnvironment.ParseSounds();
        var musicTypes = _miscEnvironment.ParseMusicTypes();
        var textureSets = _miscEnvironment.ParseTextureSets();
        var armorAddons = _miscItems.ParseArmorAddons();
        var water = _miscEnvironment.ParseWater();
        var bodyPartData = _miscItems.ParseBodyPartData();
        var actorValueInfos = _miscGameSystems.ParseActorValueInfos();
        var combatStyles = _miscGameSystems.ParseCombatStyles();
        var lightingTemplates = _miscGameSystems.ParseLightingTemplates();
        var navMeshes = _miscGameSystems.ParseNavMeshes();
        var weather = _miscEnvironment.ParseWeather();
        Logger.Instance.Debug(
            $"  [Semantic] Specialized records: {phaseSw.Elapsed} " +
            $"(SOUN: {sounds.Count}, MUSC: {musicTypes.Count}, TXST: {textureSets.Count}, ARMA: {armorAddons.Count}, " +
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
            Eyes = eyes,
            Hair = hair,
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
            MusicTypes = musicTypes,
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
            UnparsedTypeCounts = unparsedCounts
        };

        totalSw.Stop();
        Logger.Instance.Info(
            $"[Semantic Parse] Complete. Time: {totalSw.Elapsed}, Records: {result.TotalRecordsParsed}");

        progress?.Report((100, "Complete"));
        return result;
    }

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
    public List<NpcRecord> ParseNpcs()
    {
        return _actors.ParseNpcs();
    }

    public List<CreatureRecord> ParseCreatures()
    {
        return _actors.ParseCreatures();
    }

    public List<FactionRecord> ParseFactions()
    {
        return _actors.ParseFactions();
    }

    public List<RaceRecord> ParseRaces()
    {
        return _actors.ParseRaces();
    }

    // Items
    public List<WeaponRecord> ParseWeapons()
    {
        return _weapons.ParseWeapons();
    }

    public List<ArmorRecord> ParseArmor()
    {
        return _items.ParseArmor();
    }

    public List<AmmoRecord> ParseAmmo()
    {
        return _consumables.ParseAmmo();
    }

    public List<ConsumableRecord> ParseConsumables()
    {
        return _consumables.ParseConsumables();
    }

    public List<MiscItemRecord> ParseMiscItems()
    {
        return _items.ParseMiscItems();
    }

    public List<KeyRecord> ParseKeys()
    {
        return _items.ParseKeys();
    }

    public List<ContainerRecord> ParseContainers()
    {
        return _items.ParseContainers();
    }

    // Dialogue
    public List<QuestRecord> ParseQuests()
    {
        return _dialogue.ParseQuests();
    }

    public List<DialogTopicRecord> ParseDialogTopics()
    {
        return _dialogue.ParseDialogTopics();
    }

    public List<DialogueRecord> ParseDialogue()
    {
        return _dialogue.ParseDialogue();
    }

    public DialogueTreeResult BuildDialogueTrees(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics,
        List<QuestRecord> quests)
    {
        return _dialogue.BuildDialogueTrees(dialogues, topics, quests);
    }

    // Text
    public List<NoteRecord> ParseNotes()
    {
        return _text.ParseNotes();
    }

    public List<BookRecord> ParseBooks()
    {
        return _text.ParseBooks();
    }

    public List<TerminalRecord> ParseTerminals()
    {
        return _text.ParseTerminals();
    }

    public List<MessageRecord> ParseMessages()
    {
        return _text.ParseMessages();
    }

    // Scripts
    public List<ScriptRecord> ParseScripts()
    {
        return _scripts.ParseScripts();
    }

    // Effects
    public List<PerkRecord> ParsePerks()
    {
        return _effects.ParsePerks();
    }

    public List<SpellRecord> ParseSpells()
    {
        return _effects.ParseSpells();
    }

    public List<EnchantmentRecord> ParseEnchantments()
    {
        return _effects.ParseEnchantments();
    }

    public List<BaseEffectRecord> ParseBaseEffects()
    {
        return _effects.ParseBaseEffects();
    }

    public List<ProjectileRecord> ParseProjectiles()
    {
        return _combatEffects.ParseProjectiles();
    }

    public List<ExplosionRecord> ParseExplosions()
    {
        return _combatEffects.ParseExplosions();
    }

    // World
    public List<CellRecord> ParseCells()
    {
        return _world.ParseCells();
    }

    public List<WorldspaceRecord> ParseWorldspaces()
    {
        return _world.ParseWorldspaces();
    }

    public List<PlacedReference> ExtractMapMarkers()
    {
        return _world.ExtractMapMarkers();
    }

    // Misc
    public List<GameSettingRecord> ParseGameSettings()
    {
        return _misc.ParseGameSettings();
    }

    public List<GlobalRecord> ParseGlobals()
    {
        return _miscBasicTypes.ParseGlobals();
    }

    public List<WeaponModRecord> ParseWeaponMods()
    {
        return _miscItems.ParseWeaponMods();
    }

    public List<RecipeRecord> ParseRecipes()
    {
        return _miscItems.ParseRecipes();
    }

    public List<ChallengeRecord> ParseChallenges()
    {
        return _miscBasicTypes.ParseChallenges();
    }

    public List<ReputationRecord> ParseReputations()
    {
        return _miscBasicTypes.ParseReputations();
    }

    public List<ClassRecord> ParseClasses()
    {
        return _miscBasicTypes.ParseClasses();
    }

    public List<LeveledListRecord> ParseLeveledLists()
    {
        return _miscCollections.ParseLeveledLists();
    }

    public List<FormListRecord> ParseFormLists()
    {
        return _miscCollections.ParseFormLists();
    }

    public List<ActivatorRecord> ParseActivators()
    {
        return _miscWorldObjects.ParseActivators();
    }

    public List<LightRecord> ParseLights()
    {
        return _miscWorldObjects.ParseLights();
    }

    public List<DoorRecord> ParseDoors()
    {
        return _miscWorldObjects.ParseDoors();
    }

    public List<StaticRecord> ParseStatics()
    {
        return _miscStaticObjects.ParseStatics();
    }

    public List<FurnitureRecord> ParseFurniture()
    {
        return _miscStaticObjects.ParseFurniture();
    }

    // AI
    public List<PackageRecord> ParsePackages()
    {
        return _ai.ParsePackages();
    }
}

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

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

    /// <summary>
    ///     Creates a parser over an ESM scan result, optionally backed by a memory-mapped
    ///     view for resolving subrecord data and runtime DMP structures.
    /// </summary>
    public RecordParser(
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? formIdCorrelations = null,
        MemoryMappedViewAccessor? accessor = null,
        long fileSize = 0,
        MinidumpInfo? minidumpInfo = null)
        : this(new RecordParserContext(scanResult, formIdCorrelations, accessor, fileSize, minidumpInfo))
    {
    }

    /// <summary>
    ///     Creates a parser backed by an arbitrary <see cref="IMemoryAccessor" /> (e.g. a raw
    ///     byte buffer rather than a memory-mapped file).
    /// </summary>
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
    public RecordCollection ParseAll(
        IProgress<(int percent, string phase)>? progress = null,
        IReadOnlySet<uint>? residentRecoveryMasterFormIds = null)
    {
        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

        // Morrowind (TES3) plugins use a flat record stream with 4-byte subrecord sizes and entirely
        // different record/subrecord layouts — the TES4 typed handlers below would misread every byte.
        // Route them to the dedicated TES3 parser, which decodes each record into a GenericEsmRecord
        // with typed Fields that the same show / list / stats surface consumes.
        if (_context.ScanResult.IsTes3)
        {
            progress?.Report((0, "Parsing TES3 records..."));
            var tes3Result = new Tes3.Tes3RecordParser(_context).ParseAll();
            totalSw.Stop();
            Logger.Instance.Info(
                $"[Semantic Parse] Complete (TES3). Time: {totalSw.Elapsed}, Records: {tes3Result.TotalRecordsProcessed}");
            progress?.Report((100, "Complete"));
            return tes3Result;
        }

        // Parsed record types — single source of truth lives in EsmParsedRecordTypes, which is
        // completeness-checked against RecordCollection so a new parser can't silently end up
        // mislabeled as "not parsed". Anything not in this set falls into UnparsedTypeCounts below.
        var parsedTypes = EsmParsedRecordTypes.Codes;

        // Count all record types and compute unparsed counts
        var allTypeCounts = _context.ScanResult.MainRecords
            .GroupBy(r => r.RecordType)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var unparsedCounts = allTypeCounts
            .Where(kvp => !parsedTypes.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // === Runtime enrichment phases (LAND, REFR, worldspace cell maps) ===
        RuntimeDataEnricher.EnrichLandRecords(_context);

        // Resident-ref recovery: the form-table scan misses heap REFRs whose hash buckets
        // weren't dumped. Sweep the heap for master refs the table missed and append them so
        // EnrichPlacedReferences reads them and the authority/parent attribution places them in
        // their owning cell (instead of the cell-merge deleting them as "uncaptured").
        if (residentRecoveryMasterFormIds is { Count: > 0 })
        {
            phaseSw.Restart();
            var beforeCount = _context.ScanResult.RuntimeRefrFormEntries.Count;
            var recovered = RuntimeRefrHeapSweep.AppendMissingMasterRefrEntries(
                _context, residentRecoveryMasterFormIds);
            Logger.Instance.Info(
                $"  [Semantic] Resident-ref recovery: swept {recovered:N0} master REFR(s) the form-table " +
                $"missed (master set {residentRecoveryMasterFormIds.Count:N0}, table entries {beforeCount:N0}, " +
                $"regions {_context.MinidumpInfo?.MemoryRegions.Count ?? 0:N0}); {phaseSw.Elapsed}");
        }

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
        progress?.Report((5, "Parsing characters..."));
        phaseSw.Restart();
        var npcs = _actors.ParseNpcs();
        var creatures = _actors.ParseCreatures();
        var races = _actors.ParseRaces();
        var factions = _actors.ParseFactions();
        Logger.Instance.Debug(
            $"  [Semantic] Characters: {phaseSw.Elapsed} (NPCs: {npcs.Count}, Creatures: {creatures.Count}, Races: {races.Count}, Factions: {factions.Count})");

        progress?.Report((15, "Parsing items..."));
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
        progress?.Report((30, "Parsing dialogue..."));
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

        _dialogue.BackfillDialogTopicPromptText(dialogues, dialogTopics);
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

        progress?.Report((55, "Parsing abilities..."));
        phaseSw.Restart();
        var perks = _effects.ParsePerks();
        var spells = _effects.ParseSpells();
        Logger.Instance.Debug(
            $"  [Semantic] Abilities: {phaseSw.Elapsed} (Perks: {perks.Count}, Spells: {spells.Count})");

        progress?.Report((60, "Parsing world data..."));
        phaseSw.Restart();
        var cells = _world.ParseCells();
        var cellTime = phaseSw.Elapsed;
        var worldspaces = _world.ParseWorldspaces();

        // DMP fallback: infer worldspace membership from cell grid coordinates when GRUP data is absent
        if (_context.ScanResult.CellToWorldspaceMap.Count == 0 && worldspaces.Count > 0)
        {
            WorldRecordHandler.InferCellWorldspaces(cells, worldspaces);
            WorldRecordHandler.ResolveRuntimeAnchoredCellRuns(cells, worldspaces, _context.RuntimeWorldspaceCellMaps);
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
                WorldRecordHandler.ResolveRuntimeAnchoredCellRuns(cells, worldspaces,
                    _context.RuntimeWorldspaceCellMaps);
            }
        }

        EsmLandEnricher.EnrichLandRecordsWithCellWorldspaces(_context.ScanResult, cells);
        CellRecordHandler.AttachTerrainDataFromLandRecords(cells, _context.ScanResult);

        // Persistent-ref redistribution: move persistent refs with valid world positions into
        // their real exterior tile (per-worldspace grid lookup with parsed-cell fallback and
        // synthetic-tile creation when neither source covers the destination grid). Must run
        // after CreateVirtualCells so that Phase 1 has had a chance to populate persistent
        // cells via ParentCellFormId.
        var redistributed = CellLinkageHandler.RedistributePersistentRefs(cells, _context);
        if (redistributed > 0)
        {
            CellRecordHandler.ResolveDoorLinks(cells);
            Logger.Instance.Debug(
                $"  [Semantic] Redistributed {redistributed} persistent ref(s) to owning exterior tiles");
        }

        // Engine-time duplicate cleanup: when several runtime grid cells of the same worldspace
        // each contain a copy of the same persistent ref (same FormID + same position), keep it
        // in the grid-correct cell and drop the rest. Driven by Bethesda engine sharing
        // persistent refs into every currently-loaded grid cell's listReferences array.
        var deduplicated = PersistentRefRedistributor.DeduplicateRuntimeCellRefs(cells, _context);
        if (deduplicated > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Deduplicated {deduplicated} runtime-shared ref copy(s) across grid cells");
        }

        var globallyDeduplicated = PersistentRefRedistributor.DeduplicatePlacedRefsToBestCell(cells);
        if (globallyDeduplicated > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Deduplicated {globallyDeduplicated} duplicate placed ref copy(s) across cells");
        }

        // DMP fallback (after all worldspace inference): backfill worldspace ownership from the
        // runtime pCellMap for any exterior cell still null, then reclassify worldspace-less,
        // grid-less "exterior" cells as interior (cut/dev cells captured without the interior bit,
        // e.g. HiddenValley*Sim). Guarded to the DMP path — ESM GRUP parsing already has exact
        // ownership and never produces these malformed cells.
        if (_context.ScanResult.CellToWorldspaceMap.Count == 0)
        {
            WorldRecordHandler.AssignRuntimeCellMapOwners(cells, _context.RuntimeWorldspaceCellMaps);
            WorldRecordHandler.NormalizeStructurallyInteriorCells(cells);
            // NOTE: co-located virtual-orphan-cell merge runs later in PluginBuilder, where the
            // master FormID set is available to exclude master cells as keepers (merging orphan
            // refs into a live master persistent cell crashes the engine on GridCellArray attach).
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

        progress?.Report((80, "Parsing game data..."));
        phaseSw.Restart();
        var gameSettings = _misc.ParseGameSettings();
        var globals = _miscBasicTypes.ParseGlobals();
        var enchantments = _effects.ParseEnchantments();
        var baseEffects = _effects.ParseBaseEffects();
        var weaponMods = _miscItems.ParseWeaponMods();
        var recipes = _miscItems.ParseRecipes();
        var recipeCategories = _miscBasicTypes.ParseRecipeCategories();
        var constructibleObjects = _miscItems.ParseConstructibleObjects();
        var challenges = _miscBasicTypes.ParseChallenges();
        var reputations = _miscBasicTypes.ParseReputations();
        var projectiles = _combatEffects.ParseProjectiles();
        _combatEffects.EnrichProjectilesWithRuntime(projectiles);
        var explosions = _combatEffects.ParseExplosions();
        var messages = _text.ParseMessages();
        var classes = _miscBasicTypes.ParseClasses();
        var eyes = _miscBasicTypes.ParseEyes();
        var hair = _miscBasicTypes.ParseHair();
        var headParts = _miscBasicTypes.ParseHeadParts();
        var voiceTypes = _miscBasicTypes.ParseVoiceTypes();
        var menuIcons = _miscBasicTypes.ParseMenuIcons();
        var formLists = _miscCollections.ParseFormLists();
        // activators, doors, furniture already parsed above (before script cross-ref chains)
        var lights = _miscWorldObjects.ParseLights();
        var statics = _miscStaticObjects.ParseStatics();
        var staticCollections = _miscStaticObjects.ParseStaticCollections();
        Logger.Instance.Debug($"  [Semantic] Game data: {phaseSw.Elapsed} (16 types)");

        progress?.Report((85, "Parsing generic records..."));
        phaseSw.Restart();
        var genericTypes = new[]
        {
            "MSTT", "TACT", "CAMS", "ANIO", "IPDS", "EFSH", "RGDL", "LSCR",
            "ASPC", "MSET", "CHIP", "CSNO", "DOBJ", "ADDN", "TREE", "IMAD",
            "IDLM", "PWAT",
            // SCOL is parsed via the typed _miscStaticObjects.ParseStaticCollections() path.
            // CLMT is parsed via the typed _miscEnvironment.ParseClimate() path (atmosphere data).
            // Small PDB-defined types with no parity-relevant fields beyond identity.
            "IMGS", "GRAS", "AMEF",
            // FLOR (Flora): harvestable plants. ESM-side coverage so flora carries through
            // (EDID/FULL/MODL/OBND + PFIG ingredient, SNAM sound, SCRI script via schema/fallback)
            // even when no DMP is supplied — previously only the runtime RuntimeFlorReader populated it.
            "FLOR"
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

        progress?.Report((88, "Parsing specialized records..."));
        phaseSw.Restart();
        var sounds = _miscEnvironment.ParseSounds();
        var musicTypes = _miscEnvironment.ParseMusicTypes();
        var textureSets = _miscEnvironment.ParseTextureSets();
        MergeRuntimeLandTextureSets(textureSets);
        var landTextures = _miscEnvironment.ParseLandscapeTextures();
        MergeRuntimeLandTextures(landTextures);
        var grasses = _miscEnvironment.ParseGrass();
        var armorAddons = _miscItems.ParseArmorAddons();
        var water = _miscEnvironment.ParseWater();
        var bodyPartData = _miscItems.ParseBodyPartData();
        var actorValueInfos = _miscGameSystems.ParseActorValueInfos();
        var combatStyles = _miscGameSystems.ParseCombatStyles();
        var lightingTemplates = _miscGameSystems.ParseLightingTemplates();
        var navMeshes = _miscGameSystems.ParseNavMeshes();
        var encounterZones = _miscGameSystems.ParseEncounterZones();
        var weather = _miscEnvironment.ParseWeather();
        var climate = _miscEnvironment.ParseClimate();
        var loadScreenTypes = _miscEnvironment.ParseLoadScreenTypes();
        var audioLocationControllers = _miscEnvironment.ParseAudioLocationControllers();
        var idleAnimations = _ai.ParseIdleAnimations();
        var cameraPaths = _world.ParseCameraPaths();
        var impactData = _combatEffects.ParseImpactData();
        var placedGrenades = _world.ParsePlacedGrenades();
        var regions = _world.ParseRegions();
        var caravanCards = _miscGameSystems.ParseCaravanCards();
        var caravanMoney = _miscGameSystems.ParseCaravanMoney();
        var debris = _miscWorldObjects.ParseDebris();
        var ingredients = _miscItems.ParseIngredients();
        var navMeshInfoMaps = _miscGameSystems.ParseNavMeshInfoMaps();
        var caravanDecks = _miscGameSystems.ParseCaravanDecks();
        var radiationStages = _miscGameSystems.ParseRadiationStages();
        var dehydrationStages = _miscGameSystems.ParseDehydrationStages();
        var hungerStages = _miscGameSystems.ParseHungerStages();
        var sleepDeprivationStages = _miscGameSystems.ParseSleepDeprivationStages();
        Logger.Instance.Debug(
            $"  [Semantic] Specialized records: {phaseSw.Elapsed} " +
            $"(SOUN: {sounds.Count}, MUSC: {musicTypes.Count}, TXST: {textureSets.Count}, LTEX: {landTextures.Count}, ARMA: {armorAddons.Count}, " +
            $"WATR: {water.Count}, BPTD: {bodyPartData.Count}, AVIF: {actorValueInfos.Count}, " +
            $"CSTY: {combatStyles.Count}, LGTM: {lightingTemplates.Count}, " +
            $"NAVM: {navMeshes.Count}, ECZN: {encounterZones.Count}, WTHR: {weather.Count})");

        // === Build object bounds/model indexes and enrich placed references ===
        var modelIndex = new Dictionary<uint, string>();
        ObjectIndexBuilder.BuildAndEnrich(
            statics, activators, doors, lights, furniture,
            staticCollections,
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
            RecipeCategories = recipeCategories,
            ConstructibleObjects = constructibleObjects,
            Challenges = challenges,
            Reputations = reputations,
            Projectiles = projectiles,
            Explosions = explosions,
            Messages = messages,
            Classes = classes,
            Eyes = eyes,
            Hair = hair,
            HeadParts = headParts,
            VoiceTypes = voiceTypes,
            MenuIcons = menuIcons,
            LoadScreenTypes = loadScreenTypes,
            IdleAnimations = idleAnimations,
            CameraPaths = cameraPaths,
            ImpactData = impactData,
            AudioLocationControllers = audioLocationControllers,
            PlacedGrenades = placedGrenades,
            Regions = regions,
            CaravanCards = caravanCards,
            CaravanMoney = caravanMoney,
            Debris = debris,
            Ingredients = ingredients,
            NavMeshInfoMaps = navMeshInfoMaps,
            CaravanDecks = caravanDecks,
            RadiationStages = radiationStages,
            DehydrationStages = dehydrationStages,
            HungerStages = hungerStages,
            SleepDeprivationStages = sleepDeprivationStages,
            FormLists = formLists,
            Activators = activators,
            Lights = lights,
            Doors = doors,
            Statics = statics,
            StaticCollections = staticCollections,
            Furniture = furniture,

            // AI
            Packages = packages,

            // Generic
            GenericRecords = genericRecords,

            // Specialized record models
            Sounds = sounds,
            MusicTypes = musicTypes,
            TextureSets = textureSets,
            LandTextures = landTextures,
            Grasses = grasses,
            ArmorAddons = armorAddons,
            Water = water,
            BodyPartData = bodyPartData,
            ActorValueInfos = actorValueInfos,
            CombatStyles = combatStyles,
            LightingTemplates = lightingTemplates,
            NavMeshes = navMeshes,
            EncounterZones = encounterZones,
            Weather = weather,
            Climate = climate,

            ModelPathIndex = modelIndex,
            FormIdToEditorId = new Dictionary<uint, string>(_context.FormIdToEditorId),
            FormIdToDisplayName = _context.BuildFormIdToDisplayNameMap(),
            RuntimeWorldspaceMaps = _context.RuntimeWorldspaceCellMaps != null
                ? new Dictionary<uint, RuntimeWorldspaceData>(_context.RuntimeWorldspaceCellMaps)
                : [],
            TotalRecordsProcessed = _context.ScanResult.MainRecords.Count,
            UnparsedTypeCounts = unparsedCounts
        };

        totalSw.Stop();
        Logger.Instance.Info(
            $"[Semantic Parse] Complete. Time: {totalSw.Elapsed}, Records: {result.TotalRecordsParsed}");

        if (_context.PartiallyRecoveredFormIds.Count > 0)
        {
            Logger.Instance.Info(
                "[Semantic Parse] Recovered {0} truncated compressed record(s) from the memory dump " +
                "(leading subrecords preserved; set FALLOUT_ESM_DISABLE_DMP_PARTIAL_RECOVERY=1 to disable).",
                _context.PartiallyRecoveredFormIds.Count);
        }

        progress?.Report((100, "Complete"));
        return result;
    }

    private void MergeRuntimeLandTextures(List<LandscapeTextureRecord> landTextures)
    {
        if (_context.ScanResult.LandRecords.Count == 0)
        {
            return;
        }

        var knownFormIds = new HashSet<uint>(landTextures.Select(l => l.FormId));
        var added = 0;
        foreach (var runtimeTexture in _context.ScanResult.LandRecords.SelectMany(l => l.RuntimeLandTextures))
        {
            if (runtimeTexture.FormId == 0 || !knownFormIds.Add(runtimeTexture.FormId))
            {
                continue;
            }

            landTextures.Add(runtimeTexture);
            added++;

            if (!string.IsNullOrEmpty(runtimeTexture.EditorId))
            {
                _context.FormIdToEditorId.TryAdd(runtimeTexture.FormId, runtimeTexture.EditorId);
            }
        }

        if (added > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Added {added} LTEX record(s) from runtime LAND texture pointers " +
                $"(total: {landTextures.Count})");
        }
    }

    private void MergeRuntimeLandTextureSets(List<TextureSetRecord> textureSets)
    {
        if (_context.ScanResult.LandRecords.Count == 0)
        {
            return;
        }

        var knownFormIds = new HashSet<uint>(textureSets.Select(t => t.FormId));
        var added = 0;
        foreach (var runtimeTextureSet in _context.ScanResult.LandRecords.SelectMany(l => l.RuntimeTextureSets))
        {
            if (runtimeTextureSet.FormId == 0 || !knownFormIds.Add(runtimeTextureSet.FormId))
            {
                continue;
            }

            textureSets.Add(runtimeTextureSet);
            added++;

            if (!string.IsNullOrEmpty(runtimeTextureSet.EditorId))
            {
                _context.FormIdToEditorId.TryAdd(runtimeTextureSet.FormId, runtimeTextureSet.EditorId);
            }
        }

        if (added > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Added {added} TXST record(s) from runtime LAND texture-set pointers " +
                $"(total: {textureSets.Count})");
        }
    }

    /// <summary>Looks up the editor ID for a FormID, or null if unknown.</summary>
    public string? GetEditorId(uint formId)
    {
        return _context.GetEditorId(formId);
    }

    /// <summary>Looks up the FormID for an editor ID, or null if unknown.</summary>
    public uint? GetFormId(string editorId)
    {
        return _context.GetFormId(editorId);
    }

    /// <summary>Gets the detected main record for a FormID, or null if not present.</summary>
    public DetectedMainRecord? GetRecord(uint formId)
    {
        return _context.GetRecord(formId);
    }

    /// <summary>Enumerates all detected main records of the given 4-character type.</summary>
    public IEnumerable<DetectedMainRecord> GetRecordsByType(string recordType)
    {
        return _context.GetRecordsByType(recordType);
    }

    // Actors
    /// <summary>Parses all NPC (actor) records.</summary>
    public List<NpcRecord> ParseNpcs()
    {
        return _actors.ParseNpcs();
    }

    /// <summary>Parses all creature records.</summary>
    public List<CreatureRecord> ParseCreatures()
    {
        return _actors.ParseCreatures();
    }

    /// <summary>Parses all faction records.</summary>
    public List<FactionRecord> ParseFactions()
    {
        return _actors.ParseFactions();
    }

    /// <summary>Parses all race records.</summary>
    public List<RaceRecord> ParseRaces()
    {
        return _actors.ParseRaces();
    }

    // Items
    /// <summary>Parses all weapon records.</summary>
    public List<WeaponRecord> ParseWeapons()
    {
        return _weapons.ParseWeapons();
    }

    /// <summary>Parses all armor records.</summary>
    public List<ArmorRecord> ParseArmor()
    {
        return _items.ParseArmor();
    }

    /// <summary>Parses all ammunition records.</summary>
    public List<AmmoRecord> ParseAmmo()
    {
        return _consumables.ParseAmmo();
    }

    /// <summary>Parses all consumable (ALCH) records.</summary>
    public List<ConsumableRecord> ParseConsumables()
    {
        return _consumables.ParseConsumables();
    }

    /// <summary>Parses all miscellaneous-item records.</summary>
    public List<MiscItemRecord> ParseMiscItems()
    {
        return _items.ParseMiscItems();
    }

    /// <summary>Parses all key records.</summary>
    public List<KeyRecord> ParseKeys()
    {
        return _items.ParseKeys();
    }

    /// <summary>Parses all container records.</summary>
    public List<ContainerRecord> ParseContainers()
    {
        return _items.ParseContainers();
    }

    // Dialogue
    /// <summary>Parses all quest records.</summary>
    public List<QuestRecord> ParseQuests()
    {
        return _dialogue.ParseQuests();
    }

    /// <summary>Parses all dialogue-topic (DIAL) records.</summary>
    public List<DialogTopicRecord> ParseDialogTopics()
    {
        return _dialogue.ParseDialogTopics();
    }

    /// <summary>Parses all dialogue-response (INFO) records.</summary>
    public List<DialogueRecord> ParseDialogue()
    {
        return _dialogue.ParseDialogue();
    }

    /// <summary>
    ///     Builds the hierarchical dialogue tree linking topics, responses, and quests.
    /// </summary>
    public DialogueTreeResult BuildDialogueTrees(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics,
        List<QuestRecord> quests)
    {
        return _dialogue.BuildDialogueTrees(dialogues, topics, quests);
    }

    // Text
    /// <summary>Parses all note records.</summary>
    public List<NoteRecord> ParseNotes()
    {
        return _text.ParseNotes();
    }

    /// <summary>Parses all book records.</summary>
    public List<BookRecord> ParseBooks()
    {
        return _text.ParseBooks();
    }

    /// <summary>Parses all terminal records.</summary>
    public List<TerminalRecord> ParseTerminals()
    {
        return _text.ParseTerminals();
    }

    /// <summary>Parses all message records.</summary>
    public List<MessageRecord> ParseMessages()
    {
        return _text.ParseMessages();
    }

    // Scripts
    /// <summary>Parses all script records.</summary>
    public List<ScriptRecord> ParseScripts()
    {
        return _scripts.ParseScripts();
    }

    // Effects
    /// <summary>Parses all perk records.</summary>
    public List<PerkRecord> ParsePerks()
    {
        return _effects.ParsePerks();
    }

    /// <summary>Parses all spell records.</summary>
    public List<SpellRecord> ParseSpells()
    {
        return _effects.ParseSpells();
    }

    /// <summary>Parses all enchantment records.</summary>
    public List<EnchantmentRecord> ParseEnchantments()
    {
        return _effects.ParseEnchantments();
    }

    /// <summary>Parses all base magic-effect records.</summary>
    public List<BaseEffectRecord> ParseBaseEffects()
    {
        return _effects.ParseBaseEffects();
    }

    /// <summary>Parses all projectile records.</summary>
    public List<ProjectileRecord> ParseProjectiles()
    {
        return _combatEffects.ParseProjectiles();
    }

    /// <summary>Parses all explosion records.</summary>
    public List<ExplosionRecord> ParseExplosions()
    {
        return _combatEffects.ParseExplosions();
    }

    // World
    /// <summary>Parses all cell records.</summary>
    public List<CellRecord> ParseCells()
    {
        return _world.ParseCells();
    }

    /// <summary>Parses all worldspace records.</summary>
    public List<WorldspaceRecord> ParseWorldspaces()
    {
        return _world.ParseWorldspaces();
    }

    /// <summary>Extracts placed map-marker references from the world records.</summary>
    public List<PlacedReference> ExtractMapMarkers()
    {
        return _world.ExtractMapMarkers();
    }

    // Misc
    /// <summary>Parses all game-setting (GMST) records.</summary>
    public List<GameSettingRecord> ParseGameSettings()
    {
        return _misc.ParseGameSettings();
    }

    /// <summary>Parses all global-variable records.</summary>
    public List<GlobalRecord> ParseGlobals()
    {
        return _miscBasicTypes.ParseGlobals();
    }

    /// <summary>Parses all weapon-mod records.</summary>
    public List<WeaponModRecord> ParseWeaponMods()
    {
        return _miscItems.ParseWeaponMods();
    }

    /// <summary>Parses all recipe records.</summary>
    public List<RecipeRecord> ParseRecipes()
    {
        return _miscItems.ParseRecipes();
    }

    /// <summary>Parses all challenge records.</summary>
    public List<ChallengeRecord> ParseChallenges()
    {
        return _miscBasicTypes.ParseChallenges();
    }

    /// <summary>Parses all reputation records.</summary>
    public List<ReputationRecord> ParseReputations()
    {
        return _miscBasicTypes.ParseReputations();
    }

    /// <summary>Parses all class records.</summary>
    public List<ClassRecord> ParseClasses()
    {
        return _miscBasicTypes.ParseClasses();
    }

    /// <summary>Parses all leveled-list records.</summary>
    public List<LeveledListRecord> ParseLeveledLists()
    {
        return _miscCollections.ParseLeveledLists();
    }

    /// <summary>Parses all form-list records.</summary>
    public List<FormListRecord> ParseFormLists()
    {
        return _miscCollections.ParseFormLists();
    }

    /// <summary>Parses all activator records.</summary>
    public List<ActivatorRecord> ParseActivators()
    {
        return _miscWorldObjects.ParseActivators();
    }

    /// <summary>Parses all light records.</summary>
    public List<LightRecord> ParseLights()
    {
        return _miscWorldObjects.ParseLights();
    }

    /// <summary>Parses all door records.</summary>
    public List<DoorRecord> ParseDoors()
    {
        return _miscWorldObjects.ParseDoors();
    }

    /// <summary>Parses all static-object records.</summary>
    public List<StaticRecord> ParseStatics()
    {
        return _miscStaticObjects.ParseStatics();
    }

    /// <summary>Parses all static-collection (SCOL) records.</summary>
    public List<StaticCollectionRecord> ParseStaticCollections()
    {
        return _miscStaticObjects.ParseStaticCollections();
    }

    /// <summary>Parses all furniture records.</summary>
    public List<FurnitureRecord> ParseFurniture()
    {
        return _miscStaticObjects.ParseFurniture();
    }

    /// <summary>
    ///     Parses all records of a generic (schema/identity-only) type into <see cref="GenericEsmRecord" />s.
    ///     Exposed primarily for focused parser coverage tests (e.g. FLOR); the full parse drives this
    ///     internally via the genericTypes loop in <see cref="ParseAll" />.
    /// </summary>
    public List<GenericEsmRecord> ParseGenericRecords(string recordType)
    {
        return _misc.ParseGenericRecords(recordType);
    }

    // AI
    /// <summary>Parses all AI-package records.</summary>
    public List<PackageRecord> ParsePackages()
    {
        return _ai.ParsePackages();
    }
}

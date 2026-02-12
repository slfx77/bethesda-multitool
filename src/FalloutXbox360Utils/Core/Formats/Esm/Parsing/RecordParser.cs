using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reconstructs semantic game data from ESM record scan results and runtime memory structures.
/// </summary>
public sealed partial class RecordParser
{
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
        _scanResult = scanResult;
        _accessor = accessor;
        _fileSize = fileSize;

        // Create runtime struct reader if we have both accessor and minidump info
        if (accessor != null && minidumpInfo != null && fileSize > 0)
        {
            _runtimeReader = new RuntimeStructReader(accessor, fileSize, minidumpInfo);
        }

        // Build FormID lookup from main records
        _recordsByFormId = scanResult.MainRecords
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        // Build EditorID lookups from ESM EDID subrecords or pre-built correlations.
        // Note: formIdCorrelations MUST contain EditorIDs (not display names/FullNames).
        // If display names leak in here, EditorId == FullName on reconstructed records.
        _formIdToEditorId = formIdCorrelations != null
            ? new Dictionary<uint, string>(formIdCorrelations)
            : BuildFormIdToEditorIdMap(scanResult);

        // Merge runtime EditorIDs (from hash table walk or brute-force scan)
        // These provide additional FormID -> EditorID mappings not found in ESM subrecords
        foreach (var entry in scanResult.RuntimeEditorIds)
        {
            if (entry.FormId != 0 && !_formIdToEditorId.ContainsKey(entry.FormId))
            {
                _formIdToEditorId[entry.FormId] = entry.EditorId;
            }
        }

        // Inject well-known engine FormIDs (hardcoded in executable, not in ESM/hash table)
        _formIdToEditorId.TryAdd(0x00000007, "PlayerRef");
        _formIdToEditorId.TryAdd(0x00000014, "Player");

        _editorIdToFormId = _formIdToEditorId
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);
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
        var allTypeCounts = _scanResult.MainRecords
            .GroupBy(r => r.RecordType)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var unreconstructedCounts = allTypeCounts
            .Where(kvp => !reconstructedTypes.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // Enrich LAND records with runtime cell coordinates for heightmap stitching
        if (_runtimeReader != null)
        {
            var runtimeLandData = _runtimeReader.ReadAllRuntimeLandData(_scanResult.RuntimeEditorIds);
            if (runtimeLandData.Count > 0)
            {
                EsmRecordFormat.EnrichLandRecordsWithRuntimeData(_scanResult, runtimeLandData);
                Logger.Instance.Debug(
                    $"  [Semantic] Enriched {runtimeLandData.Count} LAND records with runtime cell coordinates");
            }
        }

        // Pre-scan all records for FULL subrecords (display names) so that
        // unreconstructed types (HAIR, EYES, CSTY, etc.) have names available for display
        progress?.Report((2, "Scanning display names..."));
        phaseSw.Restart();
        CaptureAllFullNames();
        Logger.Instance.Debug($"  [Semantic] Display names: {phaseSw.Elapsed} ({_formIdToFullName.Count} names captured)");

        // Build weapons and ammo first, then cross-reference for projectile data
        progress?.Report((5, "Reconstructing characters..."));
        phaseSw.Restart();
        var npcs = ReconstructNpcs();
        var creatures = ReconstructCreatures();
        var races = ReconstructRaces();
        var factions = ReconstructFactions();
        Logger.Instance.Debug($"  [Semantic] Characters: {phaseSw.Elapsed} (NPCs: {npcs.Count}, Creatures: {creatures.Count}, Races: {races.Count}, Factions: {factions.Count})");

        progress?.Report((15, "Reconstructing items..."));
        phaseSw.Restart();
        var weapons = ReconstructWeapons();
        var ammo = ReconstructAmmo();
        EnrichAmmoWithProjectileModels(weapons, ammo);
        EnrichWeaponsWithProjectileData(weapons);
        var armor = ReconstructArmor();
        var consumables = ReconstructConsumables();
        var miscItems = ReconstructMiscItems();
        var keys = ReconstructKeys();
        var containers = ReconstructContainers();
        Logger.Instance.Debug($"  [Semantic] Items: {phaseSw.Elapsed} (Weapons: {weapons.Count}, Armor: {armor.Count}, Ammo: {ammo.Count}, Consumables: {consumables.Count}, Misc: {miscItems.Count}, Keys: {keys.Count}, Containers: {containers.Count})");

        // Build dialogue data, then construct the tree hierarchy
        progress?.Report((30, "Reconstructing dialogue..."));
        phaseSw.Restart();
        var quests = ReconstructQuests();
        var dialogTopics = ReconstructDialogTopics();
        var dialogues = ReconstructDialogue();

        if (_runtimeReader != null)
        {
            // Walk TESTopic.m_listQuestInfo to link INFO->Topic->Quest and create new INFO records
            MergeRuntimeDialogueTopicLinks(dialogues, dialogTopics);

            // Enrich all dialogue records (ESM + new from topic walk) with runtime hash table data
            // (EditorId, speaker, quest, flags, difficulty, prompt from TESTopicInfo struct).
            // This runs AFTER the topic walk so new entries get enriched too.
            MergeRuntimeDialogueData(dialogues);
        }
        else if (_accessor != null)
        {
            // Pure ESM files: link INFO→DIAL by file offset ordering (GROUP nesting).
            // In ESM files, INFO records follow their parent DIAL in the GRUP structure.
            // Runtime dumps use MergeRuntimeDialogueTopicLinks instead.
            LinkInfoToTopicsByGroupOrder(dialogues, dialogTopics);
        }

        // Propagate topic-level speaker (TNAM from ESM DIAL records) to INFO records without a speaker
        PropagateTopicSpeakers(dialogues, dialogTopics);

        // Propagate from attributed siblings within same topic, then quest-level
        PropagateTopicSiblingSpeakers(dialogues);
        PropagateQuestSpeakers(dialogues);

        // EditorID convention matching for remaining unlinked dialogues
        LinkDialogueByEditorIdConvention(dialogues, quests);
        Logger.Instance.Debug($"  [Semantic] Dialogue: {phaseSw.Elapsed} (Quests: {quests.Count}, Topics: {dialogTopics.Count}, Dialogues: {dialogues.Count})");

        progress?.Report((45, "Building dialogue trees..."));
        phaseSw.Restart();
        var dialogueTree = BuildDialogueTrees(dialogues, dialogTopics, quests);
        var notes = ReconstructNotes();
        var books = ReconstructBooks();
        var terminals = ReconstructTerminals();
        var scripts = ReconstructScripts();
        Logger.Instance.Debug($"  [Semantic] Trees/text: {phaseSw.Elapsed} (Notes: {notes.Count}, Books: {books.Count}, Terminals: {terminals.Count}, Scripts: {scripts.Count})");

        progress?.Report((55, "Reconstructing abilities..."));
        phaseSw.Restart();
        var perks = ReconstructPerks();
        var spells = ReconstructSpells();
        Logger.Instance.Debug($"  [Semantic] Abilities: {phaseSw.Elapsed} (Perks: {perks.Count}, Spells: {spells.Count})");

        progress?.Report((60, "Reconstructing world data..."));
        phaseSw.Restart();
        var cells = ReconstructCells();
        var cellTime = phaseSw.Elapsed;
        var worldspaces = ReconstructWorldspaces();
        LinkCellsToWorldspaces(cells, worldspaces);
        var packages = ReconstructPackages();
        var resolvedCount = SpawnPositionResolver.ResolveSpawnPositions(cells, packages, npcs, creatures);
        var mapMarkers = ExtractMapMarkers();
        var leveledLists = ReconstructLeveledLists();
        Logger.Instance.Debug($"  [Semantic] World: {phaseSw.Elapsed} (Cells: {cells.Count} in {cellTime}, Worldspaces: {worldspaces.Count}, Packages: {packages.Count}, SpawnResolved: {resolvedCount}, MapMarkers: {mapMarkers.Count}, LeveledLists: {leveledLists.Count})");

        progress?.Report((80, "Reconstructing game data..."));
        phaseSw.Restart();
        var gameSettings = ReconstructGameSettings();
        var globals = ReconstructGlobals();
        var enchantments = ReconstructEnchantments();
        var baseEffects = ReconstructBaseEffects();
        var weaponMods = ReconstructWeaponMods();
        var recipes = ReconstructRecipes();
        var challenges = ReconstructChallenges();
        var reputations = ReconstructReputations();
        var projectiles = ReconstructProjectiles();
        var explosions = ReconstructExplosions();
        var messages = ReconstructMessages();
        var classes = ReconstructClasses();
        var formLists = ReconstructFormLists();
        var activators = ReconstructActivators();
        var lights = ReconstructLights();
        var doors = ReconstructDoors();
        var statics = ReconstructStatics();
        var furniture = ReconstructFurniture();
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

            FormIdToEditorId = new Dictionary<uint, string>(_formIdToEditorId),
            FormIdToDisplayName = BuildFormIdToDisplayNameMap(),
            TotalRecordsProcessed = _scanResult.MainRecords.Count,
            UnreconstructedTypeCounts = unreconstructedCounts
        };

        totalSw.Stop();
        Logger.Instance.Info($"[Semantic Reconstruction] Complete. Time: {totalSw.Elapsed}, Records: {result.TotalRecordsReconstructed}");

        progress?.Report((100, "Complete"));
        return result;
    }

    #endregion

    #region Fields

    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly Dictionary<string, uint> _editorIdToFormId;
    private readonly long _fileSize;
    private readonly Dictionary<uint, string> _formIdToEditorId;
    private readonly Dictionary<uint, string> _formIdToFullName = new();
    private readonly Dictionary<uint, DetectedMainRecord> _recordsByFormId;
    private readonly RuntimeStructReader? _runtimeReader;
    private readonly EsmRecordScanResult _scanResult;

    #endregion

    #region Public API - Lookup Methods

    /// <summary>
    ///     Get the Editor ID for a FormID.
    /// </summary>
    public string? GetEditorId(uint formId)
    {
        return _formIdToEditorId.GetValueOrDefault(formId);
    }

    /// <summary>
    ///     Get the FormID for an Editor ID.
    /// </summary>
    public uint? GetFormId(string editorId)
    {
        return _editorIdToFormId.TryGetValue(editorId, out var formId) ? formId : null;
    }

    /// <summary>
    ///     Get a main record by FormID.
    /// </summary>
    public DetectedMainRecord? GetRecord(uint formId)
    {
        return _recordsByFormId.GetValueOrDefault(formId);
    }

    /// <summary>
    ///     Get all main records of a specific type.
    /// </summary>
    public IEnumerable<DetectedMainRecord> GetRecordsByType(string recordType)
    {
        return _scanResult.MainRecords.Where(r => r.RecordType == recordType);
    }

    #endregion

    #region Private Helper Methods - FormID/EditorID Mapping

    private static Dictionary<uint, string> BuildFormIdToEditorIdMap(EsmRecordScanResult scanResult)
    {
        var map = new Dictionary<uint, string>();

        // Correlate EDID subrecords to nearby main record headers
        foreach (var edid in scanResult.EditorIds)
        {
            // Find the closest main record header before this EDID
            var nearestRecord = scanResult.MainRecords
                .Where(r => r.Offset < edid.Offset && edid.Offset < r.Offset + r.DataSize + 24)
                .OrderByDescending(r => r.Offset)
                .FirstOrDefault();

            if (nearestRecord != null && !map.ContainsKey(nearestRecord.FormId))
            {
                map[nearestRecord.FormId] = edid.Name;
            }
        }

        return map;
    }

    /// <summary>
    ///     Pre-scan all records for FULL subrecords, capturing display names
    ///     for types that are not individually reconstructed (HAIR, EYES, CSTY, etc.).
    ///     Runs once before specific reconstruction methods, so those methods can
    ///     override entries with more accurate data if needed.
    /// </summary>
    private void CaptureAllFullNames()
    {
        if (_accessor == null)
        {
            return;
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            foreach (var record in _scanResult.MainRecords)
            {
                if (_formIdToFullName.ContainsKey(record.FormId))
                {
                    continue;
                }

                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    if (sub.Signature == "FULL" && sub.DataLength > 0)
                    {
                        var name = EsmStringUtils.ReadNullTermString(
                            data.AsSpan(sub.DataOffset, sub.DataLength));
                        if (!string.IsNullOrEmpty(name))
                        {
                            _formIdToFullName.TryAdd(record.FormId, name);
                        }

                        break;
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Build a FormID to display name (FullName) mapping from runtime hash table entries.
    ///     These display names come from TESFullName.cFullName read during the hash table walk.
    /// </summary>
    private Dictionary<uint, string> BuildFormIdToDisplayNameMap()
    {
        // Start with FullNames collected during ESM subrecord parsing
        var map = new Dictionary<uint, string>(_formIdToFullName);

        // Overlay runtime display names (from hash table walk) — these may be more
        // up-to-date for memory dumps but are empty for standalone ESM files
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormId != 0 && !string.IsNullOrEmpty(entry.DisplayName))
            {
                map.TryAdd(entry.FormId, entry.DisplayName);
            }
        }

        return map;
    }

    /// <summary>
    ///     Resolve a FormID to EditorID or display name, checking all available sources.
    /// </summary>
    private string? ResolveFormName(uint formId)
    {
        if (formId == 0)
        {
            return null;
        }

        if (_formIdToEditorId.TryGetValue(formId, out var editorId))
        {
            return editorId;
        }

        // Check runtime display names
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormId == formId)
            {
                return entry.DisplayName ?? entry.EditorId;
            }
        }

        return null;
    }

    #endregion

    #region Private Helper Methods - Name/Subrecord Lookups

    private string? FindFullNameNear(long recordOffset)
    {
        return _scanResult.FullNames
            .Where(f => Math.Abs(f.Offset - recordOffset) < 500)
            .OrderBy(f => Math.Abs(f.Offset - recordOffset))
            .FirstOrDefault()?.Text;
    }

    /// <summary>
    ///     Find FULL subrecord strictly within a record's data bounds (no accessor).
    ///     Only accepts FULL offsets between record header and record end.
    /// </summary>
    private string? FindFullNameInRecordBounds(DetectedMainRecord record)
    {
        var dataStart = record.Offset + 24;
        var dataEnd = dataStart + record.DataSize;

        return _scanResult.FullNames
            .Where(f => f.Offset >= dataStart && f.Offset < dataEnd)
            .OrderBy(f => f.Offset)
            .FirstOrDefault()?.Text;
    }

    private ActorBaseSubrecord? FindActorBaseNear(long recordOffset)
    {
        return _scanResult.ActorBases
            .Where(a => Math.Abs(a.Offset - recordOffset) < 500)
            .OrderBy(a => Math.Abs(a.Offset - recordOffset))
            .FirstOrDefault();
    }

    /// <summary>
    ///     Reads record data from the accessor, decompressing if the record is compressed.
    ///     Returns null if data cannot be read or decompression fails.
    /// </summary>
    private (byte[] Data, int Size)? ReadRecordData(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return null;
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        if (!record.IsCompressed)
        {
            return (buffer, dataSize);
        }

        if (dataSize <= 4)
        {
            return null;
        }

        var decompressed = EsmParser.DecompressRecordData(
            buffer.AsSpan(0, dataSize), record.IsBigEndian);
        return decompressed != null ? (decompressed, decompressed.Length) : null;
    }

    #endregion
}

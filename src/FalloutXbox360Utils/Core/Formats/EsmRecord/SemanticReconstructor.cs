using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Reconstructs semantic game objects from detected ESM records in memory dumps.
///     Links FormIDs, correlates subrecords, and builds complete objects like NPCs, Quests, Notes, etc.
/// </summary>
public sealed class SemanticReconstructor
{
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly Dictionary<string, uint> _editorIdToFormId;
    private readonly long _fileSize;
    private readonly Dictionary<uint, string> _formIdToEditorId;
    private readonly Dictionary<uint, DetectedMainRecord> _recordsByFormId;
    private readonly RuntimeStructReader? _runtimeReader;
    private readonly EsmRecordScanResult _scanResult;

    /// <summary>
    ///     Creates a new SemanticReconstructor with scan results and optional memory-mapped access.
    /// </summary>
    /// <param name="scanResult">The ESM record scan results from EsmRecordFormat.</param>
    /// <param name="formIdCorrelations">FormID to Editor ID correlations.</param>
    /// <param name="accessor">Optional memory-mapped accessor for reading additional record data.</param>
    /// <param name="fileSize">Size of the memory dump file.</param>
    /// <param name="minidumpInfo">Optional minidump info for runtime struct reading (pointer following).</param>
    public SemanticReconstructor(
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

        // Build EditorID lookups from ESM EDID subrecords
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

        _editorIdToFormId = _formIdToEditorId
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);
    }

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

    /// <summary>
    ///     Perform full semantic reconstruction of all supported record types.
    /// </summary>
    public SemanticReconstructionResult ReconstructAll()
    {
        // Reconstructed record types
        var reconstructedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NPC_", "CREA", "RACE", "FACT",
            "QUST", "DIAL", "INFO", "NOTE", "BOOK", "TERM",
            "WEAP", "ARMO", "AMMO", "ALCH", "MISC", "KEYM", "CONT",
            "PERK", "SPEL", "CELL", "WRLD", "GMST",
            "GLOB", "ENCH", "MGEF", "IMOD", "RCPE", "CHAL", "REPU",
            "PROJ", "EXPL", "MESG", "CLAS"
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

        // Build weapons and ammo first, then cross-reference for projectile data
        var weapons = ReconstructWeapons();
        var ammo = ReconstructAmmo();
        EnrichAmmoWithProjectileModels(weapons, ammo);
        EnrichWeaponsWithProjectileData(weapons);

        // Build dialogue data, then construct the tree hierarchy
        var quests = ReconstructQuests();
        var dialogTopics = ReconstructDialogTopics();
        var dialogues = ReconstructDialogue();

        if (_runtimeReader != null)
        {
            // Walk TESTopic.m_listQuestInfo to link INFO→Topic→Quest and create new INFO records
            MergeRuntimeDialogueTopicLinks(dialogues, dialogTopics);

            // Enrich all dialogue records (ESM + new from topic walk) with runtime hash table data
            // (EditorId, speaker, quest, flags, difficulty, prompt from TESTopicInfo struct).
            // This runs AFTER the topic walk so new entries get enriched too.
            MergeRuntimeDialogueData(dialogues);
        }

        // Propagate topic-level speaker (TNAM from ESM DIAL records) to INFO records without a speaker
        PropagateTopicSpeakers(dialogues, dialogTopics);

        // EditorID convention matching for remaining unlinked dialogues
        LinkDialogueByEditorIdConvention(dialogues, quests);

        var dialogueTree = BuildDialogueTrees(dialogues, dialogTopics, quests);

        return new SemanticReconstructionResult
        {
            // Characters
            Npcs = ReconstructNpcs(),
            Creatures = ReconstructCreatures(),
            Races = ReconstructRaces(),
            Factions = ReconstructFactions(),

            // Quests and Dialogue
            Quests = quests,
            DialogTopics = dialogTopics,
            Dialogues = dialogues,
            DialogueTree = dialogueTree,
            Notes = ReconstructNotes(),
            Books = ReconstructBooks(),
            Terminals = ReconstructTerminals(),

            // Items
            Weapons = weapons,
            Armor = ReconstructArmor(),
            Ammo = ammo,
            Consumables = ReconstructConsumables(),
            MiscItems = ReconstructMiscItems(),
            Keys = ReconstructKeys(),
            Containers = ReconstructContainers(),

            // Abilities
            Perks = ReconstructPerks(),
            Spells = ReconstructSpells(),

            // World
            Cells = ReconstructCells(),
            Worldspaces = ReconstructWorldspaces(),
            MapMarkers = ExtractMapMarkers(),
            LeveledLists = ReconstructLeveledLists(),

            // Game Data
            GameSettings = ReconstructGameSettings(),
            Globals = ReconstructGlobals(),
            Enchantments = ReconstructEnchantments(),
            BaseEffects = ReconstructBaseEffects(),
            WeaponMods = ReconstructWeaponMods(),
            Recipes = ReconstructRecipes(),
            Challenges = ReconstructChallenges(),
            Reputations = ReconstructReputations(),
            Projectiles = ReconstructProjectiles(),
            Explosions = ReconstructExplosions(),
            Messages = ReconstructMessages(),
            Classes = ReconstructClasses(),

            FormIdToEditorId = new Dictionary<uint, string>(_formIdToEditorId),
            FormIdToDisplayName = BuildFormIdToDisplayNameMap(),
            TotalRecordsProcessed = _scanResult.MainRecords.Count,
            UnreconstructedTypeCounts = unreconstructedCounts
        };
    }

    /// <summary>
    ///     Build a FormID to display name (FullName) mapping from runtime hash table entries.
    ///     These display names come from TESFullName.cFullName read during the hash table walk.
    /// </summary>
    private Dictionary<uint, string> BuildFormIdToDisplayNameMap()
    {
        var map = new Dictionary<uint, string>();

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
    ///     Reconstruct all NPC records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data (typically thousands of NPCs vs ~7 ESM records).
    /// </summary>
    public List<ReconstructedNpc> ReconstructNpcs()
    {
        var npcs = new List<ReconstructedNpc>();
        var npcRecords = GetRecordsByType("NPC_").ToList();

        // Track FormIDs from ESM records to avoid duplicates when merging runtime data
        var esmFormIds = new HashSet<uint>();

        if (_accessor == null)
        {
            // Without accessor, use already-parsed subrecords from scan result
            foreach (var record in npcRecords)
            {
                var npc = ReconstructNpcFromScanResult(record);
                if (npc != null)
                {
                    npcs.Add(npc);
                    esmFormIds.Add(npc.FormId);
                }
            }
        }
        else
        {
            // With accessor, read full record data for better reconstruction
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in npcRecords)
                {
                    var npc = ReconstructNpcFromAccessor(record, buffer);
                    if (npc != null)
                    {
                        npcs.Add(npc);
                        esmFormIds.Add(npc.FormId);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge NPCs from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x2A || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var npc = _runtimeReader.ReadRuntimeNpc(entry);
                if (npc != null)
                {
                    npcs.Add(npc);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} NPCs from runtime struct reading " +
                    $"(total: {npcs.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return npcs;
    }

    /// <summary>
    ///     Reconstruct all Quest records from the scan result.
    /// </summary>
    public List<ReconstructedQuest> ReconstructQuests()
    {
        var quests = new List<ReconstructedQuest>();
        var questRecords = GetRecordsByType("QUST").ToList();

        if (_accessor == null)
        {
            foreach (var record in questRecords)
            {
                var quest = ReconstructQuestFromScanResult(record);
                if (quest != null)
                {
                    quests.Add(quest);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(32768); // Quests can be larger
            try
            {
                foreach (var record in questRecords)
                {
                    var quest = ReconstructQuestFromAccessor(record, buffer);
                    if (quest != null)
                    {
                        quests.Add(quest);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge quests from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(quests.Select(q => q.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x47 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var quest = _runtimeReader.ReadRuntimeQuest(entry);
                if (quest != null)
                {
                    quests.Add(quest);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} quests from runtime struct reading " +
                    $"(total: {quests.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return quests;
    }

    /// <summary>
    ///     Reconstruct all Dialogue (INFO) records from the scan result.
    /// </summary>
    public List<ReconstructedDialogue> ReconstructDialogue()
    {
        var dialogues = new List<ReconstructedDialogue>();
        var infoRecords = GetRecordsByType("INFO").ToList();

        if (_accessor == null)
        {
            foreach (var record in infoRecords)
            {
                var dialogue = ReconstructDialogueFromScanResult(record);
                if (dialogue != null)
                {
                    dialogues.Add(dialogue);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in infoRecords)
                {
                    var dialogue = ReconstructDialogueFromAccessor(record, buffer);
                    if (dialogue != null)
                    {
                        dialogues.Add(dialogue);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Deduplicate by FormID — same record can appear in both BE and LE memory regions.
        // Keep the version with the most response data.
        var deduped = dialogues
            .GroupBy(d => d.FormId)
            .Select(g => g.OrderByDescending(d => d.Responses.Count)
                .ThenByDescending(d => d.Responses.Sum(r => r.Text?.Length ?? 0))
                .First())
            .ToList();

        return deduped;
    }

    /// <summary>
    ///     Enrich dialogue records with runtime TESTopicInfo data from the hash table.
    ///     Matches dialogue FormIDs against RuntimeEditorIds to find corresponding entries,
    ///     then reads the TESTopicInfo struct to get speaker, quest, flags, difficulty, and prompt.
    ///     Only enriches existing records — new entries are created by MergeRuntimeDialogueTopicLinks.
    /// </summary>
    private void MergeRuntimeDialogueData(List<ReconstructedDialogue> dialogues)
    {
        // Build FormID → runtime entry lookup from hash table
        var runtimeByFormId = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.TesFormOffset.HasValue)
            {
                runtimeByFormId.TryAdd(entry.FormId, entry);
            }
        }

        var mergedCount = 0;

        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            if (!runtimeByFormId.TryGetValue(dialogue.FormId, out var entry))
            {
                continue;
            }

            var runtimeInfo = _runtimeReader!.ReadRuntimeDialogueInfo(entry);
            if (runtimeInfo == null)
            {
                continue;
            }

            dialogues[i] = dialogue with
            {
                EditorId = dialogue.EditorId ?? entry.EditorId,
                PromptText = runtimeInfo.PromptText ?? dialogue.PromptText,
                InfoIndex = runtimeInfo.InfoIndex,
                InfoFlags = runtimeInfo.InfoFlags,
                InfoFlagsExt = runtimeInfo.InfoFlagsExt,
                Difficulty = runtimeInfo.Difficulty > 0 ? runtimeInfo.Difficulty : dialogue.Difficulty,
                SpeakerFormId = runtimeInfo.SpeakerFormId ?? dialogue.SpeakerFormId,
                QuestFormId = runtimeInfo.QuestFormId ?? dialogue.QuestFormId
            };
            mergedCount++;
        }

        Logger.Instance.Debug(
            $"  [Semantic] Runtime INFO enrich: {mergedCount}/{dialogues.Count} enriched " +
            $"(hashEntries={runtimeByFormId.Count})");
    }

    /// <summary>
    ///     Walk TESTopic.m_listQuestInfo for each runtime DIAL entry to build
    ///     Topic → Quest and Topic → [INFO] mappings. Sets TopicFormId and QuestFormId
    ///     on all linked dialogue records.
    /// </summary>
    private void MergeRuntimeDialogueTopicLinks(
        List<ReconstructedDialogue> dialogues,
        List<ReconstructedDialogTopic> topics)
    {
        // Detect DIAL FormType (same logic as MergeRuntimeDialogTopicData)
        var knownDialFormIds = new HashSet<uint>(topics.Select(t => t.FormId));
        byte? dialFormType = null;
        var formTypeCounts = new Dictionary<byte, int>();
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (knownDialFormIds.Contains(entry.FormId))
            {
                formTypeCounts.TryGetValue(entry.FormType, out var count);
                formTypeCounts[entry.FormType] = count + 1;
            }
        }

        if (formTypeCounts.Count > 0)
        {
            var best = formTypeCounts.MaxBy(kv => kv.Value);
            if (best.Value >= 2)
            {
                dialFormType = best.Key;
            }
        }

        if (!dialFormType.HasValue)
        {
            // Fallback: use 0x45 (empirically verified shared DIAL+INFO FormType).
            // WalkTopicQuestInfoList filters non-DIALs naturally (they won't have valid m_listQuestInfo).
            const byte candidateFormType = 0x45;
            var hasEntries = _scanResult.RuntimeEditorIds
                .Any(e => e.FormType == candidateFormType && e.TesFormOffset.HasValue);
            if (hasEntries)
            {
                dialFormType = candidateFormType;
            }
        }

        if (!dialFormType.HasValue)
        {
            return;
        }

        // Build FormID → dialogue index for updating
        var dialogueByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < dialogues.Count; i++)
        {
            dialogueByFormId.TryAdd(dialogues[i].FormId, i);
        }

        var topicLinked = 0;
        var questLinked = 0;
        var topicsWalked = 0;
        var totalInfosLinked = 0;
        var totalInfosFound = 0;
        var newInfoCount = 0;

        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormType != dialFormType.Value || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            // Walk this topic's m_listQuestInfo to get Quest→INFO mappings
            var questLinks = _runtimeReader!.WalkTopicQuestInfoList(entry);

            if (questLinks.Count == 0)
            {
                continue;
            }

            topicsWalked++;
            totalInfosFound += questLinks.Sum(l => l.InfoEntries.Count);

            foreach (var link in questLinks)
            {
                foreach (var infoEntry in link.InfoEntries)
                {
                    if (dialogueByFormId.TryGetValue(infoEntry.FormId, out var idx))
                    {
                        // Update existing dialogue record
                        var existing = dialogues[idx];
                        var updated = existing;

                        if (!existing.TopicFormId.HasValue || existing.TopicFormId.Value == 0)
                        {
                            updated = updated with { TopicFormId = entry.FormId };
                            topicLinked++;
                        }

                        if (!existing.QuestFormId.HasValue || existing.QuestFormId.Value == 0)
                        {
                            updated = updated with { QuestFormId = link.QuestFormId };
                            questLinked++;
                        }

                        if (updated != existing)
                        {
                            dialogues[idx] = updated;
                            totalInfosLinked++;
                        }
                    }
                    else
                    {
                        // Create new dialogue record from TESTopicInfo pointer
                        var runtimeInfo = _runtimeReader!.ReadRuntimeDialogueInfoFromVA(infoEntry.VirtualAddress);
                        if (runtimeInfo != null)
                        {
                            var newDialogue = new ReconstructedDialogue
                            {
                                FormId = infoEntry.FormId,
                                TopicFormId = entry.FormId,
                                QuestFormId = link.QuestFormId,
                                PromptText = runtimeInfo.PromptText,
                                InfoIndex = runtimeInfo.InfoIndex,
                                InfoFlags = runtimeInfo.InfoFlags,
                                InfoFlagsExt = runtimeInfo.InfoFlagsExt,
                                Difficulty = runtimeInfo.Difficulty,
                                SpeakerFormId = runtimeInfo.SpeakerFormId,
                                Offset = runtimeInfo.DumpOffset,
                                IsBigEndian = true
                            };
                            dialogues.Add(newDialogue);
                            dialogueByFormId.TryAdd(infoEntry.FormId, dialogues.Count - 1);
                            newInfoCount++;
                            topicLinked++;
                            questLinked++;
                        }
                    }
                }
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] Topic→Quest walk: {topicsWalked} topics, " +
            $"{totalInfosFound} INFO ptrs, {totalInfosLinked} existing linked, " +
            $"{newInfoCount} new INFOs created " +
            $"(+{topicLinked} TopicFormId, +{questLinked} QuestFormId)");
    }

    /// <summary>
    ///     Propagate topic-level speaker (TNAM) to INFO records that lack a speaker.
    ///     In Fallout NV, the speaker NPC is stored on the DIAL record's TNAM subrecord,
    ///     not per-INFO. This pass fills in SpeakerFormId for INFOs under topics with TNAM.
    /// </summary>
    private static void PropagateTopicSpeakers(
        List<ReconstructedDialogue> dialogues,
        List<ReconstructedDialogTopic> dialogTopics)
    {
        // Build TopicFormId → SpeakerFormId map from topics that have TNAM
        var topicSpeakers = new Dictionary<uint, uint>();
        foreach (var topic in dialogTopics)
        {
            if (topic.SpeakerFormId.HasValue && topic.SpeakerFormId.Value != 0)
            {
                topicSpeakers.TryAdd(topic.FormId, topic.SpeakerFormId.Value);
            }
        }

        if (topicSpeakers.Count == 0)
        {
            return;
        }

        var propagated = 0;
        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            // Skip if already has a speaker
            if (dialogue.SpeakerFormId.HasValue && dialogue.SpeakerFormId.Value != 0)
            {
                continue;
            }

            // Skip if no topic link
            if (!dialogue.TopicFormId.HasValue || dialogue.TopicFormId.Value == 0)
            {
                continue;
            }

            // Look up topic-level speaker
            if (topicSpeakers.TryGetValue(dialogue.TopicFormId.Value, out var speakerFormId))
            {
                dialogues[i] = dialogue with { SpeakerFormId = speakerFormId };
                propagated++;
            }
        }

        if (propagated > 0)
        {
            Logger.Instance.Debug($"  Propagated topic-level speaker (TNAM) to {propagated:N0} dialogue records");
        }
    }

    /// <summary>
    ///     Link dialogue records to quests by matching EditorID naming conventions.
    ///     Fallout NV INFO EditorIDs follow patterns like "{QuestPrefix}Topic{NNN}"
    ///     or "{QuestPrefix}{Speaker}Topic{NNN}". This is a heuristic fallback for
    ///     records not linked by the precise m_listQuestInfo walking.
    /// </summary>
    private void LinkDialogueByEditorIdConvention(
        List<ReconstructedDialogue> dialogues,
        List<ReconstructedQuest> quests)
    {
        // Build quest EditorID → FormID index from the reconstructed quests list.
        // Quests already have EditorIDs from ESM scan + runtime merge.
        var questPrefixes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in quests)
        {
            if (!string.IsNullOrEmpty(quest.EditorId))
            {
                questPrefixes.TryAdd(quest.EditorId, quest.FormId);
            }
        }

        // Sort quest prefixes by length descending for longest-match-first
        var sortedPrefixes = questPrefixes
            .OrderByDescending(kv => kv.Key.Length)
            .ToList();

        var linked = 0;
        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            // Skip if already has QuestFormId
            if (dialogue.QuestFormId.HasValue && dialogue.QuestFormId.Value != 0)
            {
                continue;
            }

            // Skip if no EditorID to match
            if (string.IsNullOrEmpty(dialogue.EditorId))
            {
                continue;
            }

            // Find longest matching quest prefix
            foreach (var (prefix, questFormId) in sortedPrefixes)
            {
                if (dialogue.EditorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && dialogue.EditorId.Length > prefix.Length)
                {
                    dialogues[i] = dialogue with { QuestFormId = questFormId };
                    linked++;
                    break;
                }
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] EditorID convention matching: {linked} dialogues linked to quests " +
            $"({sortedPrefixes.Count} quest prefixes)");
    }

    /// <summary>
    ///     Build hierarchical dialogue trees: Quest → Topic → INFO chains with cross-topic links.
    ///     Uses TopicFormId (from TPIC subrecord), QuestFormId (from QSTI or runtime), and
    ///     linking subrecords (TCLT/AddTopics) to build a navigable tree structure.
    /// </summary>
    public DialogueTreeResult BuildDialogueTrees(
        List<ReconstructedDialogue> dialogues,
        List<ReconstructedDialogTopic> topics,
        List<ReconstructedQuest> quests)
    {
        // 1. Build topicFormId → List<ReconstructedDialogue> index
        var infosByTopic = new Dictionary<uint, List<ReconstructedDialogue>>();
        var unlinkedInfos = new List<ReconstructedDialogue>();

        foreach (var d in dialogues)
        {
            if (d.TopicFormId.HasValue && d.TopicFormId.Value != 0)
            {
                if (!infosByTopic.TryGetValue(d.TopicFormId.Value, out var list))
                {
                    list = [];
                    infosByTopic[d.TopicFormId.Value] = list;
                }

                list.Add(d);
            }
            else
            {
                unlinkedInfos.Add(d);
            }
        }

        // 2. Build lookups
        var topicById = topics.ToDictionary(t => t.FormId, t => t);
        var questById = quests.ToDictionary(q => q.FormId, q => q);

        // 3. Order INFOs within each topic by InfoIndex (runtime), falling back to original order
        foreach (var (_, infos) in infosByTopic)
        {
            infos.Sort((a, b) => a.InfoIndex.CompareTo(b.InfoIndex));
        }

        // 4. Build TopicDialogueNode for each known topic
        var topicNodes = new Dictionary<uint, TopicDialogueNode>();

        // Include all topics that have INFOs or ESM DIAL records
        var allTopicIds = new HashSet<uint>(infosByTopic.Keys);
        foreach (var t in topics)
        {
            allTopicIds.Add(t.FormId);
        }

        foreach (var topicId in allTopicIds)
        {
            topicById.TryGetValue(topicId, out var topic);
            var topicName = topic?.FullName ?? topic?.EditorId ?? ResolveFormName(topicId);

            var infos = infosByTopic.GetValueOrDefault(topicId, []);
            var infoNodes = infos.Select(info => new InfoDialogueNode
            {
                Info = info,
                LinkedTopics = []
            }).ToList();

            topicNodes[topicId] = new TopicDialogueNode
            {
                Topic = topic,
                TopicFormId = topicId,
                TopicName = topicName,
                InfoChain = infoNodes
            };
        }

        // 5. Cross-link: fill in LinkedTopics for each InfoDialogueNode
        foreach (var (_, topicNode) in topicNodes)
        {
            foreach (var infoNode in topicNode.InfoChain)
            {
                var linkedIds = new HashSet<uint>();
                foreach (var linkId in infoNode.Info.LinkToTopics)
                {
                    linkedIds.Add(linkId);
                }

                foreach (var addId in infoNode.Info.AddTopics)
                {
                    linkedIds.Add(addId);
                }

                foreach (var linkedId in linkedIds)
                {
                    if (topicNodes.TryGetValue(linkedId, out var linkedNode))
                    {
                        infoNode.LinkedTopics.Add(linkedNode);
                    }
                }
            }
        }

        // 6. Group topics by quest
        var questTrees = new Dictionary<uint, QuestDialogueNode>();
        var orphanTopics = new List<TopicDialogueNode>();

        foreach (var (_, topicNode) in topicNodes)
        {
            // Determine quest: from topic's QuestFormId, or from any INFO's QuestFormId
            var questId = topicNode.Topic?.QuestFormId;
            if (!questId.HasValue || questId.Value == 0)
            {
                questId = topicNode.InfoChain
                    .Select(i => i.Info.QuestFormId)
                    .FirstOrDefault(q => q.HasValue && q.Value != 0);
            }

            if (questId.HasValue && questId.Value != 0)
            {
                if (!questTrees.TryGetValue(questId.Value, out var questNode))
                {
                    questById.TryGetValue(questId.Value, out var quest);
                    questNode = new QuestDialogueNode
                    {
                        QuestFormId = questId.Value,
                        QuestName = quest?.FullName ?? quest?.EditorId ?? ResolveFormName(questId.Value),
                        Topics = []
                    };
                    questTrees[questId.Value] = questNode;
                }

                questNode.Topics.Add(topicNode);
            }
            else
            {
                orphanTopics.Add(topicNode);
            }
        }

        // 7. Create orphan topic nodes for unlinked INFOs (no TopicFormId)
        if (unlinkedInfos.Count > 0)
        {
            // Group unlinked INFOs by quest, create synthetic topic nodes
            var unlinkedByQuest = unlinkedInfos
                .GroupBy(d => d.QuestFormId ?? 0)
                .OrderBy(g => g.Key);

            foreach (var group in unlinkedByQuest)
            {
                var infoNodes = group
                    .OrderBy(d => d.InfoIndex)
                    .ThenBy(d => d.EditorId ?? "")
                    .Select(info => new InfoDialogueNode
                    {
                        Info = info,
                        LinkedTopics = []
                    }).ToList();

                var syntheticTopic = new TopicDialogueNode
                {
                    Topic = null,
                    TopicFormId = 0,
                    TopicName = "(Unlinked Responses)",
                    InfoChain = infoNodes
                };

                if (group.Key != 0)
                {
                    if (!questTrees.TryGetValue(group.Key, out var questNode))
                    {
                        questById.TryGetValue(group.Key, out var quest);
                        questNode = new QuestDialogueNode
                        {
                            QuestFormId = group.Key,
                            QuestName = quest?.FullName ?? quest?.EditorId ?? ResolveFormName(group.Key),
                            Topics = []
                        };
                        questTrees[group.Key] = questNode;
                    }

                    questNode.Topics.Add(syntheticTopic);
                }
                else
                {
                    orphanTopics.Add(syntheticTopic);
                }
            }
        }

        // Sort topics within each quest by priority (if available) then by name
        foreach (var (_, questNode) in questTrees)
        {
            questNode.Topics.Sort((a, b) =>
            {
                var pa = a.Topic?.Priority ?? 0f;
                var pb = b.Topic?.Priority ?? 0f;
                var cmp = pb.CompareTo(pa); // Higher priority first
                return cmp != 0 ? cmp : string.Compare(a.TopicName, b.TopicName, StringComparison.OrdinalIgnoreCase);
            });
        }

        return new DialogueTreeResult
        {
            QuestTrees = questTrees,
            OrphanTopics = orphanTopics
        };
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

    /// <summary>
    ///     Reconstruct all Note records from the scan result.
    /// </summary>
    public List<ReconstructedNote> ReconstructNotes()
    {
        var notes = new List<ReconstructedNote>();
        var noteRecords = GetRecordsByType("NOTE").ToList();

        if (_accessor == null)
        {
            foreach (var record in noteRecords)
            {
                var note = ReconstructNoteFromScanResult(record);
                if (note != null)
                {
                    notes.Add(note);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in noteRecords)
                {
                    var note = ReconstructNoteFromAccessor(record, buffer);
                    if (note != null)
                    {
                        notes.Add(note);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge notes from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(notes.Select(n => n.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x31 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var note = _runtimeReader.ReadRuntimeNote(entry);
                if (note != null)
                {
                    notes.Add(note);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} notes from runtime struct reading " +
                    $"(total: {notes.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return notes;
    }

    /// <summary>
    ///     Reconstruct all Cell records from the scan result.
    /// </summary>
    public List<ReconstructedCell> ReconstructCells()
    {
        var cells = new List<ReconstructedCell>();
        var cellRecords = GetRecordsByType("CELL").ToList();

        // Build a lookup of placed references by proximity to cells
        var refrRecords = _scanResult.RefrRecords;

        if (_accessor == null)
        {
            foreach (var record in cellRecords)
            {
                var cell = ReconstructCellFromScanResult(record, refrRecords);
                if (cell != null)
                {
                    cells.Add(cell);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in cellRecords)
                {
                    var cell = ReconstructCellFromAccessor(record, refrRecords, buffer);
                    if (cell != null)
                    {
                        cells.Add(cell);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return cells;
    }

    /// <summary>
    ///     Reconstruct all Worldspace records from the scan result.
    /// </summary>
    public List<ReconstructedWorldspace> ReconstructWorldspaces()
    {
        var worldspaces = new List<ReconstructedWorldspace>();
        var wrldRecords = GetRecordsByType("WRLD").ToList();

        if (_accessor == null)
        {
            foreach (var record in wrldRecords)
            {
                var worldspace = ReconstructWorldspaceFromScanResult(record);
                if (worldspace != null)
                {
                    worldspaces.Add(worldspace);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in wrldRecords)
                {
                    var worldspace = ReconstructWorldspaceFromAccessor(record, buffer);
                    if (worldspace != null)
                    {
                        worldspaces.Add(worldspace);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return worldspaces;
    }

    /// <summary>
    ///     Extract map markers from REFR records that have the XMRK subrecord.
    /// </summary>
    public List<PlacedReference> ExtractMapMarkers()
    {
        var markers = new List<PlacedReference>();

        // Map markers come from REFR records with XMRK subrecord
        foreach (var refr in _scanResult.RefrRecords)
        {
            if (!refr.IsMapMarker)
            {
                continue;
            }

            var marker = new PlacedReference
            {
                FormId = refr.Header.FormId,
                BaseFormId = refr.BaseFormId,
                BaseEditorId = refr.BaseEditorId ?? GetEditorId(refr.BaseFormId),
                RecordType = refr.Header.RecordType,
                X = refr.Position?.X ?? 0,
                Y = refr.Position?.Y ?? 0,
                Z = refr.Position?.Z ?? 0,
                RotX = refr.Position?.RotX ?? 0,
                RotY = refr.Position?.RotY ?? 0,
                RotZ = refr.Position?.RotZ ?? 0,
                Scale = refr.Scale,
                OwnerFormId = refr.OwnerFormId,
                IsMapMarker = true,
                MarkerType = refr.MarkerType.HasValue ? (MapMarkerType)refr.MarkerType.Value : null,
                MarkerName = refr.MarkerName,
                Offset = refr.Header.Offset,
                IsBigEndian = refr.Header.IsBigEndian
            };

            markers.Add(marker);
        }

        return markers;
    }

    /// <summary>
    ///     Reconstruct leveled list records (LVLI/LVLN/LVLC).
    /// </summary>
    public List<ReconstructedLeveledList> ReconstructLeveledLists()
    {
        var lists = new List<ReconstructedLeveledList>();
        var lvliRecords = GetRecordsByType("LVLI").ToList();
        var lvlnRecords = GetRecordsByType("LVLN").ToList();
        var lvlcRecords = GetRecordsByType("LVLC").ToList();

        // Combine all leveled list records
        var allRecords = lvliRecords
            .Concat(lvlnRecords)
            .Concat(lvlcRecords)
            .ToList();

        if (_accessor == null)
        {
            foreach (var record in allRecords)
            {
                var list = ReconstructLeveledListFromScanResult(record);
                if (list != null)
                {
                    lists.Add(list);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in allRecords)
                {
                    var list = ReconstructLeveledListFromAccessor(record, buffer);
                    if (list != null)
                    {
                        lists.Add(list);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return lists;
    }

    private ReconstructedLeveledList? ReconstructLeveledListFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedLeveledList
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            ListType = record.RecordType,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedLeveledList? ReconstructLeveledListFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructLeveledListFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        byte chanceNone = 0;
        byte flags = 0;
        uint? globalFormId = null;
        var entries = new List<LeveledEntry>();

        // Parse subrecords
        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "LVLD" when sub.DataLength == 1:
                    chanceNone = subData[0];
                    break;

                case "LVLF" when sub.DataLength == 1:
                    flags = subData[0];
                    break;

                case "LVLG" when sub.DataLength == 4:
                    globalFormId = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    break;

                case "LVLO" when sub.DataLength == 12:
                    // LVLO: level (u16) + pad (u16) + FormID (u32) + count (u16) + pad (u16)
                    var level = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                    var formId = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData[8..]);

                    entries.Add(new LeveledEntry(level, formId, count));
                    break;
            }
        }

        return new ReconstructedLeveledList
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            ListType = record.RecordType,
            ChanceNone = chanceNone,
            Flags = flags,
            GlobalFormId = globalFormId,
            Entries = entries,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Reconstruct all Weapon records from the scan result.
    /// </summary>
    /// <summary>
    ///     Reconstruct all Weapon records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data (typically hundreds of weapons vs ~34 ESM records).
    /// </summary>
    public List<ReconstructedWeapon> ReconstructWeapons()
    {
        var weapons = new List<ReconstructedWeapon>();
        var weaponRecords = GetRecordsByType("WEAP").ToList();

        // Track FormIDs from ESM records to avoid duplicates
        var esmFormIds = new HashSet<uint>();

        if (_accessor == null)
        {
            foreach (var record in weaponRecords)
            {
                var weapon = new ReconstructedWeapon
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                };
                weapons.Add(weapon);
                esmFormIds.Add(weapon.FormId);
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in weaponRecords)
                {
                    var weapon = ReconstructWeaponFromAccessor(record, buffer);
                    if (weapon != null)
                    {
                        weapons.Add(weapon);
                        esmFormIds.Add(weapon.FormId);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge weapons from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x28 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var weapon = _runtimeReader.ReadRuntimeWeapon(entry);
                if (weapon != null)
                {
                    weapons.Add(weapon);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} weapons from runtime struct reading " +
                    $"(total: {weapons.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return weapons;
    }

    /// <summary>
    ///     Reconstruct all Armor records from the scan result.
    /// </summary>
    public List<ReconstructedArmor> ReconstructArmor()
    {
        var armor = new List<ReconstructedArmor>();
        var armorRecords = GetRecordsByType("ARMO").ToList();

        if (_accessor == null)
        {
            foreach (var record in armorRecords)
            {
                armor.Add(new ReconstructedArmor
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in armorRecords)
                {
                    var item = ReconstructArmorFromAccessor(record, buffer);
                    if (item != null)
                    {
                        armor.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge armor from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(armor.Select(a => a.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x18 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeArmor(entry);
                if (item != null)
                {
                    armor.Add(item);
                    runtimeCount++;
                }
            }
        }

        return armor;
    }

    /// <summary>
    ///     Reconstruct all Ammo records from the scan result.
    /// </summary>
    public List<ReconstructedAmmo> ReconstructAmmo()
    {
        var ammo = new List<ReconstructedAmmo>();
        var ammoRecords = GetRecordsByType("AMMO").ToList();

        if (_accessor == null)
        {
            foreach (var record in ammoRecords)
            {
                ammo.Add(new ReconstructedAmmo
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in ammoRecords)
                {
                    var item = ReconstructAmmoFromAccessor(record, buffer);
                    if (item != null)
                    {
                        ammo.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge ammo from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(ammo.Select(a => a.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x29 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeAmmo(entry);
                if (item != null)
                {
                    ammo.Add(item);
                    runtimeCount++;
                }
            }
        }

        return ammo;
    }

    /// <summary>
    ///     Cross-references weapons and ammo to populate ProjectileFormId and ProjectileModelPath
    ///     on ammo records. Each weapon has an AmmoFormId and a ProjectileFormId. We reverse-map:
    ///     ammo FormID → weapon → projectile FormID → BGSProjectile model path at dump offset +80.
    /// </summary>
    private void EnrichAmmoWithProjectileModels(
        List<ReconstructedWeapon> weapons,
        List<ReconstructedAmmo> ammo)
    {
        if (_runtimeReader == null || ammo.Count == 0)
        {
            return;
        }

        // Build: ammo FormID → projectile FormID (from weapons that reference both)
        var ammoToProjectile = new Dictionary<uint, uint>();
        foreach (var weapon in weapons)
        {
            if (weapon.AmmoFormId is > 0 && weapon.ProjectileFormId is > 0)
            {
                ammoToProjectile.TryAdd(weapon.AmmoFormId.Value, weapon.ProjectileFormId.Value);
            }
        }

        if (ammoToProjectile.Count == 0)
        {
            return;
        }

        // Build: projectile FormID → TesFormOffset (from runtime EditorID hash table)
        // PROJ = FormType 0x33
        var projectileOffsets = new Dictionary<uint, long>();
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
            {
                projectileOffsets.TryAdd(entry.FormId, entry.TesFormOffset.Value);
            }
        }

        // Enrich each ammo record with projectile FormID and model path
        var enrichedCount = 0;
        for (var i = 0; i < ammo.Count; i++)
        {
            var a = ammo[i];
            if (!ammoToProjectile.TryGetValue(a.FormId, out var projFormId))
            {
                continue;
            }

            string? projModelPath = null;
            if (projectileOffsets.TryGetValue(projFormId, out var projFileOffset))
            {
                // Read model path BSStringT at dump offset +80 (TESModel.cModel in BGSProjectile)
                projModelPath = _runtimeReader.ReadBSStringT(projFileOffset, 80);
            }

            // Create updated record with projectile data
            // (records are immutable, so we replace in the list)
            ammo[i] = a with
            {
                ProjectileFormId = projFormId,
                ProjectileModelPath = projModelPath
            };
            enrichedCount++;
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{ammo.Count} ammo records with projectile data " +
                $"({projectileOffsets.Count} projectiles in hash table)");
        }
    }

    /// <summary>
    ///     Enrich weapon records with projectile physics data (gravity, speed, range,
    ///     explosion, in-flight sounds) read from the BGSProjectile runtime struct.
    /// </summary>
    private void EnrichWeaponsWithProjectileData(List<ReconstructedWeapon> weapons)
    {
        if (_runtimeReader == null || weapons.Count == 0)
        {
            return;
        }

        // Build: projectile FormID → TesFormOffset (from runtime EditorID hash table)
        var projectileEntries = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
            {
                projectileEntries.TryAdd(entry.FormId, entry);
            }
        }

        if (projectileEntries.Count == 0)
        {
            return;
        }

        var enrichedCount = 0;
        for (var i = 0; i < weapons.Count; i++)
        {
            var weapon = weapons[i];
            if (!weapon.ProjectileFormId.HasValue)
            {
                continue;
            }

            if (!projectileEntries.TryGetValue(weapon.ProjectileFormId.Value, out var projEntry))
            {
                continue;
            }

            var projData = _runtimeReader.ReadProjectilePhysics(
                projEntry.TesFormOffset!.Value, projEntry.FormId);

            if (projData != null)
            {
                weapons[i] = weapon with { ProjectileData = projData };
                enrichedCount++;
            }
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{weapons.Count} weapons with projectile physics " +
                $"({projectileEntries.Count} projectiles in hash table)");
        }
    }

    /// <summary>
    ///     Reconstruct all Consumable (ALCH) records from the scan result.
    /// </summary>
    public List<ReconstructedConsumable> ReconstructConsumables()
    {
        var consumables = new List<ReconstructedConsumable>();
        var alchRecords = GetRecordsByType("ALCH").ToList();

        if (_accessor == null)
        {
            foreach (var record in alchRecords)
            {
                consumables.Add(new ReconstructedConsumable
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in alchRecords)
                {
                    var item = ReconstructConsumableFromAccessor(record, buffer);
                    if (item != null)
                    {
                        consumables.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge consumables from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(consumables.Select(c => c.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x2F || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeConsumable(entry);
                if (item != null)
                {
                    consumables.Add(item);
                    runtimeCount++;
                }
            }
        }

        return consumables;
    }

    /// <summary>
    ///     Reconstruct all Misc Item records from the scan result.
    /// </summary>
    public List<ReconstructedMiscItem> ReconstructMiscItems()
    {
        var miscItems = new List<ReconstructedMiscItem>();
        var miscRecords = GetRecordsByType("MISC").ToList();

        if (_accessor == null)
        {
            foreach (var record in miscRecords)
            {
                miscItems.Add(new ReconstructedMiscItem
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in miscRecords)
                {
                    var item = ReconstructMiscItemFromAccessor(record, buffer);
                    if (item != null)
                    {
                        miscItems.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge misc items from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(miscItems.Select(m => m.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x1F || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeMiscItem(entry);
                if (item != null)
                {
                    miscItems.Add(item);
                    runtimeCount++;
                }
            }
        }

        return miscItems;
    }

    /// <summary>
    ///     Reconstruct all Perk records from the scan result.
    /// </summary>
    public List<ReconstructedPerk> ReconstructPerks()
    {
        var perks = new List<ReconstructedPerk>();
        var perkRecords = GetRecordsByType("PERK").ToList();

        if (_accessor == null)
        {
            foreach (var record in perkRecords)
            {
                perks.Add(new ReconstructedPerk
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in perkRecords)
                {
                    var perk = ReconstructPerkFromAccessor(record, buffer);
                    if (perk != null)
                    {
                        perks.Add(perk);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return perks;
    }

    /// <summary>
    ///     Reconstruct all Spell records from the scan result.
    /// </summary>
    public List<ReconstructedSpell> ReconstructSpells()
    {
        var spells = new List<ReconstructedSpell>();
        var spellRecords = GetRecordsByType("SPEL").ToList();

        if (_accessor == null)
        {
            foreach (var record in spellRecords)
            {
                spells.Add(new ReconstructedSpell
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in spellRecords)
                {
                    var spell = ReconstructSpellFromAccessor(record, buffer);
                    if (spell != null)
                    {
                        spells.Add(spell);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return spells;
    }

    /// <summary>
    ///     Reconstruct all Race records from the scan result.
    /// </summary>
    public List<ReconstructedRace> ReconstructRaces()
    {
        var races = new List<ReconstructedRace>();
        var raceRecords = GetRecordsByType("RACE").ToList();

        if (_accessor == null)
        {
            foreach (var record in raceRecords)
            {
                races.Add(new ReconstructedRace
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in raceRecords)
                {
                    var race = ReconstructRaceFromAccessor(record, buffer);
                    if (race != null)
                    {
                        races.Add(race);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return races;
    }

    /// <summary>
    ///     Reconstruct all Creature records from the scan result.
    /// </summary>
    public List<ReconstructedCreature> ReconstructCreatures()
    {
        var creatures = new List<ReconstructedCreature>();
        var creatureRecords = GetRecordsByType("CREA").ToList();

        foreach (var record in creatureRecords)
        {
            creatures.Add(new ReconstructedCreature
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        // Merge creatures from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(creatures.Select(c => c.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x2B || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var creature = _runtimeReader.ReadRuntimeCreature(entry);
                if (creature != null)
                {
                    creatures.Add(creature);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} creatures from runtime struct reading " +
                    $"(total: {creatures.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return creatures;
    }

    /// <summary>
    ///     Reconstruct all Faction records from the scan result.
    /// </summary>
    public List<ReconstructedFaction> ReconstructFactions()
    {
        var factions = new List<ReconstructedFaction>();
        var factionRecords = GetRecordsByType("FACT").ToList();

        foreach (var record in factionRecords)
        {
            factions.Add(new ReconstructedFaction
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        // Merge factions from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(factions.Select(f => f.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x08 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var faction = _runtimeReader.ReadRuntimeFaction(entry);
                if (faction != null)
                {
                    factions.Add(faction);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} factions from runtime struct reading " +
                    $"(total: {factions.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return factions;
    }

    /// <summary>
    ///     Reconstruct all Book records from the scan result.
    /// </summary>
    public List<ReconstructedBook> ReconstructBooks()
    {
        var books = new List<ReconstructedBook>();
        var bookRecords = GetRecordsByType("BOOK").ToList();

        foreach (var record in bookRecords)
        {
            books.Add(new ReconstructedBook
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        return books;
    }

    /// <summary>
    ///     Reconstruct all Key records from the scan result.
    /// </summary>
    public List<ReconstructedKey> ReconstructKeys()
    {
        var keys = new List<ReconstructedKey>();
        var keyRecords = GetRecordsByType("KEYM").ToList();

        foreach (var record in keyRecords)
        {
            keys.Add(new ReconstructedKey
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        // Merge keys from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(keys.Select(k => k.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x2E || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeKey(entry);
                if (item != null)
                {
                    keys.Add(item);
                    runtimeCount++;
                }
            }
        }

        return keys;
    }

    /// <summary>
    ///     Reconstruct all Container records from the scan result.
    /// </summary>
    public List<ReconstructedContainer> ReconstructContainers()
    {
        var containers = new List<ReconstructedContainer>();
        var containerRecords = GetRecordsByType("CONT").ToList();

        // Track FormIDs from ESM records to avoid duplicates when merging runtime data
        var esmFormIds = new HashSet<uint>();

        if (_accessor == null)
        {
            // Without accessor, use basic reconstruction (no CNTO parsing)
            foreach (var record in containerRecords)
            {
                containers.Add(new ReconstructedContainer
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
                esmFormIds.Add(record.FormId);
            }
        }
        else
        {
            // With accessor, read full record data for CNTO subrecord parsing
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in containerRecords)
                {
                    var container = ReconstructContainerFromAccessor(record, buffer);
                    if (container != null)
                    {
                        containers.Add(container);
                        esmFormIds.Add(container.FormId);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge containers from runtime struct reading
        if (_runtimeReader != null)
        {
            // Enrich ESM containers with runtime contents (current game state)
            var runtimeEnrichments = new Dictionary<uint, ReconstructedContainer>();
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x1B || !esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var rtc = _runtimeReader.ReadRuntimeContainer(entry);
                if (rtc != null && rtc.Contents.Count > 0)
                {
                    runtimeEnrichments[entry.FormId] = rtc;
                }
            }

            if (runtimeEnrichments.Count > 0)
            {
                for (var i = 0; i < containers.Count; i++)
                {
                    if (runtimeEnrichments.TryGetValue(containers[i].FormId, out var rtc))
                    {
                        containers[i] = containers[i] with
                        {
                            Contents = rtc.Contents,
                            Flags = rtc.Flags,
                            ModelPath = containers[i].ModelPath ?? rtc.ModelPath,
                            Script = containers[i].Script ?? rtc.Script
                        };
                    }
                }

                Logger.Instance.Debug(
                    $"  [Semantic] Enriched {runtimeEnrichments.Count} ESM containers with runtime contents");
            }

            // Add runtime-only containers (not in ESM)
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x1B || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var container = _runtimeReader.ReadRuntimeContainer(entry);
                if (container != null)
                {
                    containers.Add(container);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} containers from runtime struct reading " +
                    $"(total: {containers.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return containers;
    }

    private ReconstructedContainer? ReconstructContainerFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedContainer
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        uint? script = null;
        var contents = new List<InventoryItem>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNTO" when sub.DataLength >= 8:
                    var itemFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    contents.Add(new InventoryItem(itemFormId, count));
                    break;
            }
        }

        return new ReconstructedContainer
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Script = script,
            Contents = contents,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Reconstruct all Terminal records from the scan result.
    /// </summary>
    public List<ReconstructedTerminal> ReconstructTerminals()
    {
        var terminals = new List<ReconstructedTerminal>();
        var terminalRecords = GetRecordsByType("TERM").ToList();

        foreach (var record in terminalRecords)
        {
            terminals.Add(new ReconstructedTerminal
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        // Merge terminals from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(terminals.Select(t => t.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x17 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var terminal = _runtimeReader.ReadRuntimeTerminal(entry);
                if (terminal != null)
                {
                    terminals.Add(terminal);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} terminals from runtime struct reading " +
                    $"(total: {terminals.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return terminals;
    }

    /// <summary>
    ///     Reconstruct all Dialog Topic records from the scan result.
    /// </summary>
    public List<ReconstructedDialogTopic> ReconstructDialogTopics()
    {
        var topics = new List<ReconstructedDialogTopic>();
        var topicRecords = GetRecordsByType("DIAL").ToList();

        if (_accessor != null)
        {
            // Use accessor-based subrecord parsing to find FULL and TNAM within DIAL record bounds
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in topicRecords)
                {
                    var fullName = FindFullNameInRecord(record, buffer);
                    var speakerFormId = FindFormIdSubrecordInRecord(record, buffer, "TNAM");
                    topics.Add(new ReconstructedDialogTopic
                    {
                        FormId = record.FormId,
                        EditorId = GetEditorId(record.FormId),
                        FullName = fullName,
                        SpeakerFormId = speakerFormId,
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            foreach (var record in topicRecords)
            {
                // Fallback: only accept FULL subrecords strictly within the DIAL record's data
                var fullName = FindFullNameInRecordBounds(record);
                topics.Add(new ReconstructedDialogTopic
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = fullName,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }

        // Deduplicate by FormID — same DIAL record can appear in both BE and LE memory regions.
        // Keep the version with the most data (prefer one with FullName, SpeakerFormId, and EditorId).
        var deduped = topics
            .GroupBy(t => t.FormId)
            .Select(g => g
                .OrderByDescending(t => t.SpeakerFormId.HasValue ? 1 : 0)
                .ThenByDescending(t => t.FullName?.Length ?? 0)
                .ThenByDescending(t => t.EditorId?.Length ?? 0)
                .First())
            .ToList();

        // Probe DIAL runtime struct layout and merge runtime data if reader available
        if (_runtimeReader != null)
        {
            MergeRuntimeDialogTopicData(deduped);
        }

        return deduped;
    }

    /// <summary>
    ///     Detect DIAL FormType from RuntimeEditorIds by cross-referencing known ESM DIAL FormIDs,
    ///     then merge runtime TESTopic struct data (type, flags, priority) into topic records.
    /// </summary>
    private void MergeRuntimeDialogTopicData(List<ReconstructedDialogTopic> topics)
    {
        // Build set of known DIAL FormIDs from ESM scan
        var knownDialFormIds = new HashSet<uint>(topics.Select(t => t.FormId));

        // Detect DIAL FormType by finding RuntimeEditorId entries matching known DIAL FormIDs
        byte? dialFormType = null;
        var formTypeCounts = new Dictionary<byte, int>();
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (knownDialFormIds.Contains(entry.FormId))
            {
                formTypeCounts.TryGetValue(entry.FormType, out var count);
                formTypeCounts[entry.FormType] = count + 1;
            }
        }

        if (formTypeCounts.Count > 0)
        {
            var best = formTypeCounts.MaxBy(kv => kv.Value);
            if (best.Value >= 2)
            {
                dialFormType = best.Key;
            }
        }

        if (!dialFormType.HasValue)
        {
            // Fallback: try FormType 0x45 (empirically verified as DIAL+INFO shared FormType).
            // Validate by attempting ReadRuntimeDialogTopic on a few candidate entries.
            const byte candidateFormType = 0x45;
            var validCount = 0;
            var testedCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != candidateFormType || !entry.TesFormOffset.HasValue)
                {
                    continue;
                }

                if (++testedCount > 20)
                {
                    break;
                }

                var probe = _runtimeReader!.ReadRuntimeDialogTopic(entry);
                if (probe != null)
                {
                    validCount++;
                }
            }

            if (validCount >= 3)
            {
                dialFormType = candidateFormType;
                Logger.Instance.Debug(
                    $"  [Semantic] DIAL FormType fallback: 0x{candidateFormType:X2} " +
                    $"({validCount}/{testedCount} passed ReadRuntimeDialogTopic validation)");
            }
        }

        if (!dialFormType.HasValue)
        {
            Logger.Instance.Debug("  [Semantic] Could not detect DIAL FormType - no runtime topic data");
            return;
        }

        Logger.Instance.Debug($"  [Semantic] Detected DIAL FormType: 0x{dialFormType.Value:X2} " +
                              $"({formTypeCounts.GetValueOrDefault(dialFormType.Value)} matches " +
                              $"from {knownDialFormIds.Count} known DIALs)");

        // Build FormID → topic index for merging
        var topicByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < topics.Count; i++)
        {
            topicByFormId.TryAdd(topics[i].FormId, i);
        }

        var mergedCount = 0;
        var newCount = 0;

        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormType != dialFormType.Value || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            var runtimeTopic = _runtimeReader!.ReadRuntimeDialogTopic(entry);
            if (runtimeTopic == null)
            {
                continue;
            }

            if (topicByFormId.TryGetValue(entry.FormId, out var idx))
            {
                // Merge runtime data into existing ESM topic
                var existing = topics[idx];
                topics[idx] = existing with
                {
                    EditorId = existing.EditorId ?? entry.EditorId,
                    FullName = existing.FullName ?? runtimeTopic.FullName,
                    TopicType = runtimeTopic.TopicType,
                    Flags = runtimeTopic.Flags,
                    ResponseCount = (int)runtimeTopic.TopicCount,
                    Priority = runtimeTopic.Priority,
                    DummyPrompt = runtimeTopic.DummyPrompt
                };
                mergedCount++;
            }
            else
            {
                // New topic from runtime only
                topics.Add(new ReconstructedDialogTopic
                {
                    FormId = entry.FormId,
                    EditorId = entry.EditorId,
                    FullName = runtimeTopic.FullName ?? entry.DisplayName,
                    TopicType = runtimeTopic.TopicType,
                    Flags = runtimeTopic.Flags,
                    ResponseCount = (int)runtimeTopic.TopicCount,
                    Priority = runtimeTopic.Priority,
                    DummyPrompt = runtimeTopic.DummyPrompt,
                    Offset = entry.TesFormOffset.Value,
                    IsBigEndian = true
                });
                newCount++;
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] Runtime DIAL merge: {mergedCount} merged, {newCount} new " +
            $"(total: {topics.Count})");
    }

    #region Private Reconstruction Methods

    private ReconstructedNpc? ReconstructNpcFromScanResult(DetectedMainRecord record)
    {
        // Find matching subrecords from scan result
        var editorId = GetEditorId(record.FormId);
        var fullName = FindFullNameNear(record.Offset);
        var stats = FindActorBaseNear(record.Offset);

        return new ReconstructedNpc
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            Stats = stats,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedNpc? ReconstructNpcFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructNpcFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        ActorBaseSubrecord? stats = null;
        uint? race = null;
        uint? script = null;
        uint? classFormId = null;
        uint? deathItem = null;
        uint? voiceType = null;
        uint? template = null;
        var factions = new List<FactionMembership>();
        var spells = new List<uint>();
        var inventory = new List<InventoryItem>();
        var packages = new List<uint>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "ACBS" when sub.DataLength == 24:
                    stats = ParseActorBase(subData, record.Offset + 24 + sub.DataOffset, record.IsBigEndian);
                    break;
                case "RNAM" when sub.DataLength == 4:
                    race = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    classFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INAM" when sub.DataLength == 4:
                    deathItem = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength == 4:
                    voiceType = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TPLT" when sub.DataLength == 4:
                    template = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 5:
                    var factionFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var rank = (sbyte)subData[4];
                    factions.Add(new FactionMembership(factionFormId, rank));
                    break;
                case "SPLO" when sub.DataLength == 4:
                    spells.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
                case "CNTO" when sub.DataLength >= 8:
                    var itemFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    inventory.Add(new InventoryItem(itemFormId, count));
                    break;
                case "PKID" when sub.DataLength == 4:
                    packages.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedNpc
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Stats = stats,
            Race = race,
            Script = script,
            Class = classFormId,
            DeathItem = deathItem,
            VoiceType = voiceType,
            Template = template,
            Factions = factions,
            Spells = spells,
            Inventory = inventory,
            Packages = packages,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedQuest? ReconstructQuestFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedQuest
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedQuest? ReconstructQuestFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructQuestFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        byte flags = 0;
        byte priority = 0;
        uint? script = null;
        var stages = new List<QuestStage>();
        var objectives = new List<QuestObjective>();

        // Track current stage/objective being built
        int? currentStageIndex = null;
        string? currentLogEntry = null;
        byte currentStageFlags = 0;
        int? currentObjectiveIndex = null;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 2:
                    flags = subData[0];
                    priority = subData[1];
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INDX" when sub.DataLength >= 2:
                    // Save previous stage if any
                    if (currentStageIndex.HasValue)
                    {
                        stages.Add(new QuestStage
                        {
                            Index = currentStageIndex.Value,
                            LogEntry = currentLogEntry,
                            Flags = currentStageFlags
                        });
                    }

                    // Start new stage
                    currentStageIndex = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData)
                        : BinaryPrimitives.ReadInt16LittleEndian(subData);
                    currentLogEntry = null;
                    currentStageFlags = 0;
                    break;
                case "CNAM": // Log entry text
                    currentLogEntry = ReadNullTermString(subData);
                    break;
                case "QSDT" when sub.DataLength >= 1:
                    currentStageFlags = subData[0];
                    break;
                case "QOBJ" when sub.DataLength >= 4:
                    // Save previous objective if any
                    if (currentObjectiveIndex.HasValue)
                    {
                        objectives.Add(new QuestObjective
                        {
                            Index = currentObjectiveIndex.Value
                        });
                    }

                    currentObjectiveIndex = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    break;
                case "NNAM": // Objective display text
                    if (currentObjectiveIndex.HasValue)
                    {
                        objectives.Add(new QuestObjective
                        {
                            Index = currentObjectiveIndex.Value,
                            DisplayText = ReadNullTermString(subData)
                        });
                        currentObjectiveIndex = null;
                    }

                    break;
            }
        }

        // Add final stage if any
        if (currentStageIndex.HasValue)
        {
            stages.Add(new QuestStage
            {
                Index = currentStageIndex.Value,
                LogEntry = currentLogEntry,
                Flags = currentStageFlags
            });
        }

        // Add final objective if any
        if (currentObjectiveIndex.HasValue)
        {
            objectives.Add(new QuestObjective
            {
                Index = currentObjectiveIndex.Value
            });
        }

        return new ReconstructedQuest
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Flags = flags,
            Priority = priority,
            Script = script,
            Stages = stages.OrderBy(s => s.Index).ToList(),
            Objectives = objectives.OrderBy(o => o.Index).ToList(),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedDialogue? ReconstructDialogueFromScanResult(DetectedMainRecord record)
    {
        // Find response texts strictly within this INFO record's data bounds
        var dataStart = record.Offset + 24; // Skip main record header
        var dataEnd = dataStart + record.DataSize;
        var responseTexts = _scanResult.ResponseTexts
            .Where(r => r.Offset >= dataStart && r.Offset < dataEnd)
            .ToList();

        var responses = responseTexts.Select(rt => new DialogueResponse
        {
            Text = rt.Text
        }).ToList();

        return new ReconstructedDialogue
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            Responses = responses,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedDialogue? ReconstructDialogueFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructDialogueFromScanResult(record);
        }

        // Clear buffer to prevent stale subrecord data from previous records
        Array.Clear(buffer, 0, dataSize);
        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        uint? topicFormId = null;
        uint? questFormId = null;
        uint? speakerFormId = null;
        uint? previousInfo = null;
        uint difficulty = 0;
        var responses = new List<DialogueResponse>();
        var linkToTopics = new List<uint>();
        var linkFromTopics = new List<uint>();
        var addTopics = new List<uint>();

        // Track current response being built
        string? currentResponseText = null;
        uint currentEmotionType = 0;
        var currentEmotionValue = 0;
        byte currentResponseNumber = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "QSTI" when sub.DataLength == 4:
                    questFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM1":
                    // Save previous response if any
                    if (currentResponseText != null)
                    {
                        responses.Add(new DialogueResponse
                        {
                            Text = currentResponseText,
                            EmotionType = currentEmotionType,
                            EmotionValue = currentEmotionValue,
                            ResponseNumber = currentResponseNumber
                        });
                    }

                    currentResponseText = ReadNullTermString(subData);
                    currentEmotionType = 0;
                    currentEmotionValue = 0;
                    break;
                case "TRDT" when sub.DataLength >= 20:
                    currentEmotionType = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    currentEmotionValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    currentResponseNumber = subData[12];
                    break;
                case "PNAM" when sub.DataLength == 4:
                    previousInfo = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ANAM" when sub.DataLength == 4:
                    speakerFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TPIC" when sub.DataLength == 4:
                    topicFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TCLT" when sub.DataLength == 4:
                {
                    var tcltFormId = ReadFormId(subData, record.IsBigEndian);
                    if (tcltFormId != 0)
                    {
                        linkToTopics.Add(tcltFormId);
                    }

                    break;
                }
                case "TCLF" when sub.DataLength == 4:
                {
                    var tclfFormId = ReadFormId(subData, record.IsBigEndian);
                    if (tclfFormId != 0)
                    {
                        linkFromTopics.Add(tclfFormId);
                    }

                    break;
                }
                case "NAME" when sub.DataLength == 4:
                {
                    var nameFormId = ReadFormId(subData, record.IsBigEndian);
                    if (nameFormId != 0)
                    {
                        addTopics.Add(nameFormId);
                    }

                    break;
                }
                case "DNAM" when sub.DataLength >= 4:
                    difficulty = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    if (difficulty > 10)
                    {
                        difficulty = 0;
                    }

                    break;
            }
        }

        // Add final response if any
        if (currentResponseText != null)
        {
            responses.Add(new DialogueResponse
            {
                Text = currentResponseText,
                EmotionType = currentEmotionType,
                EmotionValue = currentEmotionValue,
                ResponseNumber = currentResponseNumber
            });
        }

        return new ReconstructedDialogue
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            SpeakerFormId = speakerFormId,
            Responses = responses,
            PreviousInfo = previousInfo,
            Difficulty = difficulty,
            LinkToTopics = linkToTopics,
            LinkFromTopics = linkFromTopics,
            AddTopics = addTopics,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedNote? ReconstructNoteFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedNote
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            FullName = FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedNote? ReconstructNoteFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructNoteFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? text = null;
        byte noteType = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 1:
                    noteType = subData[0];
                    break;
                case "TNAM":
                    text = ReadNullTermString(subData);
                    break;
                case "DESC": // Fallback for text content
                    if (string.IsNullOrEmpty(text))
                    {
                        text = ReadNullTermString(subData);
                    }

                    break;
            }
        }

        return new ReconstructedNote
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            NoteType = noteType,
            Text = text,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedCell? ReconstructCellFromScanResult(DetectedMainRecord record,
        List<ExtractedRefrRecord> refrRecords)
    {
        // Find XCLC near this CELL record
        var cellGrid = _scanResult.CellGrids
            .FirstOrDefault(g => Math.Abs(g.Offset - record.Offset) < 200);

        // Find nearby REFRs
        var nearbyRefs = refrRecords
            .Where(r => r.Header.Offset > record.Offset && r.Header.Offset < record.Offset + 100000)
            .Take(100)
            .Select(r => new PlacedReference
            {
                FormId = r.Header.FormId,
                BaseFormId = r.BaseFormId,
                BaseEditorId = r.BaseEditorId ?? GetEditorId(r.BaseFormId),
                RecordType = r.Header.RecordType,
                X = r.Position?.X ?? 0,
                Y = r.Position?.Y ?? 0,
                Z = r.Position?.Z ?? 0,
                RotX = r.Position?.RotX ?? 0,
                RotY = r.Position?.RotY ?? 0,
                RotZ = r.Position?.RotZ ?? 0,
                Scale = r.Scale,
                OwnerFormId = r.OwnerFormId,
                Offset = r.Header.Offset,
                IsBigEndian = r.Header.IsBigEndian
            })
            .ToList();

        return new ReconstructedCell
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            GridX = cellGrid?.GridX,
            GridY = cellGrid?.GridY,
            PlacedObjects = nearbyRefs,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedCell? ReconstructCellFromAccessor(DetectedMainRecord record,
        List<ExtractedRefrRecord> refrRecords, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructCellFromScanResult(record, refrRecords);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        int? gridX = null;
        int? gridY = null;
        byte flags = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 1:
                    flags = subData[0];
                    break;
                case "XCLC" when sub.DataLength >= 8:
                    gridX = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    gridY = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    break;
            }
        }

        // Find nearby REFRs
        var nearbyRefs = refrRecords
            .Where(r => r.Header.Offset > record.Offset && r.Header.Offset < record.Offset + 100000)
            .Take(100)
            .Select(r => new PlacedReference
            {
                FormId = r.Header.FormId,
                BaseFormId = r.BaseFormId,
                BaseEditorId = r.BaseEditorId ?? GetEditorId(r.BaseFormId),
                RecordType = r.Header.RecordType,
                X = r.Position?.X ?? 0,
                Y = r.Position?.Y ?? 0,
                Z = r.Position?.Z ?? 0,
                RotX = r.Position?.RotX ?? 0,
                RotY = r.Position?.RotY ?? 0,
                RotZ = r.Position?.RotZ ?? 0,
                Scale = r.Scale,
                OwnerFormId = r.OwnerFormId,
                Offset = r.Header.Offset,
                IsBigEndian = r.Header.IsBigEndian
            })
            .ToList();

        // Find associated heightmap
        var heightmap = _scanResult.LandRecords
            .FirstOrDefault(l => l.CellX == gridX && l.CellY == gridY)?.Heightmap;

        return new ReconstructedCell
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            GridX = gridX,
            GridY = gridY,
            Flags = flags,
            PlacedObjects = nearbyRefs,
            Heightmap = heightmap,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedWorldspace? ReconstructWorldspaceFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedWorldspace
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            FullName = FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedWorldspace? ReconstructWorldspaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructWorldspaceFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        uint? parentWorldspace = null;
        uint? climate = null;
        uint? water = null;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "WNAM" when sub.DataLength == 4:
                    parentWorldspace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    climate = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM2" when sub.DataLength == 4:
                    water = ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new ReconstructedWorldspace
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ParentWorldspaceFormId = parentWorldspace,
            ClimateFormId = climate,
            WaterFormId = water,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedWeapon? ReconstructWeaponFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedWeapon
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;

        // DATA subrecord (15 bytes)
        var value = 0;
        var health = 0;
        float weight = 0;
        short damage = 0;
        byte clipSize = 0;

        // DNAM subrecord (204 bytes)
        WeaponType weaponType = 0;
        uint animationType = 0;
        var speed = 1.0f;
        float reach = 0;
        byte ammoPerShot = 1;
        float minSpread = 0;
        float spread = 0;
        float drift = 0;
        uint? ammoFormId = null;
        uint? projectileFormId = null;
        byte vatsToHitChance = 0;
        byte numProjectiles = 1;
        float minRange = 0;
        float maxRange = 0;
        float shotsPerSec = 1;
        float actionPoints = 0;
        uint strengthRequirement = 0;
        uint skillRequirement = 0;

        // CRDT subrecord
        short criticalDamage = 0;
        var criticalChance = 1.0f;
        uint? criticalEffectFormId = null;

        // Sound subrecords
        uint? pickupSoundFormId = null;
        uint? putdownSoundFormId = null;
        uint? fireSound3DFormId = null;
        uint? fireSoundDistFormId = null;
        uint? fireSound2DFormId = null;
        uint? dryFireSoundFormId = null;
        uint? idleSoundFormId = null;
        uint? equipSoundFormId = null;
        uint? unequipSoundFormId = null;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "ENAM" when sub.DataLength == 4:
                    ammoFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 15:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    health = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    damage = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[12..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[12..]);
                    clipSize = subData[14];
                    break;
                case "DNAM" when sub.DataLength >= 64:
                    // Parse key DNAM fields
                    animationType = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    speed = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    reach = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    // Animation type (DNAM byte 0, already read as uint32) is the weapon type
                    weaponType = (WeaponType)(animationType <= 11 ? animationType : 0);
                    minSpread = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[20..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[20..]);
                    spread = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[24..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[24..]);
                    // DNAM offset 36: Projectile FormID [PROJ]
                    if (sub.DataLength >= 40)
                    {
                        var projId = ReadFormId(subData[36..], record.IsBigEndian);
                        if (projId != 0)
                        {
                            projectileFormId = projId;
                        }
                    }

                    if (sub.DataLength >= 100)
                    {
                        // offset 64: Fire Rate (shots/sec), offset 68: AP override
                        shotsPerSec = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[64..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[64..]);
                        actionPoints = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[68..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[68..]);
                        // offset 44: Min Range, offset 48: Max Range
                        minRange = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[44..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[44..]);
                        maxRange = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[48..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[48..]);
                    }

                    break;
                case "CRDT" when sub.DataLength >= 12:
                    criticalDamage = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData)
                        : BinaryPrimitives.ReadInt16LittleEndian(subData);
                    criticalChance = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    criticalEffectFormId = ReadFormId(subData[8..], record.IsBigEndian);
                    break;
                // Sound subrecords — each is a single FormID [SOUN]
                case "YNAM" when sub.DataLength == 4:
                    pickupSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ZNAM" when sub.DataLength == 4:
                    putdownSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 4:
                    // SNAM is paired: Shoot 3D FormID + Shoot Dist FormID (8 bytes)
                    fireSound3DFormId = ReadFormId(subData, record.IsBigEndian);
                    if (sub.DataLength >= 8)
                    {
                        fireSoundDistFormId = ReadFormId(subData[4..], record.IsBigEndian);
                    }

                    break;
                case "XNAM" when sub.DataLength == 4:
                    fireSound2DFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TNAM" when sub.DataLength == 4:
                    dryFireSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "UNAM" when sub.DataLength == 4:
                    idleSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM9" when sub.DataLength == 4:
                    equipSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM8" when sub.DataLength == 4:
                    unequipSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new ReconstructedWeapon
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Health = health,
            Weight = weight,
            Damage = damage,
            ClipSize = clipSize,
            WeaponType = weaponType,
            AnimationType = animationType,
            Speed = speed,
            Reach = reach,
            AmmoPerShot = ammoPerShot,
            MinSpread = minSpread,
            Spread = spread,
            Drift = drift,
            AmmoFormId = ammoFormId,
            ProjectileFormId = projectileFormId,
            VatsToHitChance = vatsToHitChance,
            NumProjectiles = numProjectiles,
            MinRange = minRange,
            MaxRange = maxRange,
            ShotsPerSec = shotsPerSec,
            ActionPoints = actionPoints,
            StrengthRequirement = strengthRequirement,
            SkillRequirement = skillRequirement,
            CriticalDamage = criticalDamage,
            CriticalChance = criticalChance,
            CriticalEffectFormId = criticalEffectFormId,
            PickupSoundFormId = pickupSoundFormId,
            PutdownSoundFormId = putdownSoundFormId,
            FireSound3DFormId = fireSound3DFormId,
            FireSoundDistFormId = fireSoundDistFormId,
            FireSound2DFormId = fireSound2DFormId,
            DryFireSoundFormId = dryFireSoundFormId,
            IdleSoundFormId = idleSoundFormId,
            EquipSoundFormId = equipSoundFormId,
            UnequipSoundFormId = unequipSoundFormId,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedArmor? ReconstructArmorFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedArmor
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        var value = 0;
        var health = 0;
        float weight = 0;
        float damageThreshold = 0;
        var damageResistance = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 12:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    health = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    break;
                case "DNAM" when sub.DataLength >= 8:
                    // DNAM layout: DR (int16) + unused (2) + DT (float) + Flags (uint16) + unused (2)
                    damageResistance = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData)
                        : BinaryPrimitives.ReadInt16LittleEndian(subData);
                    damageThreshold = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
            }
        }

        return new ReconstructedArmor
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Health = health,
            Weight = weight,
            DamageThreshold = damageThreshold,
            DamageResistance = damageResistance,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedAmmo? ReconstructAmmoFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedAmmo
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        float speed = 0;
        byte flags = 0;
        uint value = 0;
        byte clipRounds = 0;
        uint? projectileFormId = null;
        float weight = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 13:
                    speed = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    flags = subData[4];
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                    clipRounds = subData[12];
                    break;
                case "DAT2" when sub.DataLength >= 8:
                    // DAT2 layout: ProjectilePerShot (U32), Projectile FormID (U32), Weight (float), ...
                    var projId = ReadFormId(subData[4..], record.IsBigEndian);
                    if (projId != 0)
                    {
                        projectileFormId = projId;
                    }

                    if (sub.DataLength >= 12)
                    {
                        weight = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    }

                    break;
            }
        }

        return new ReconstructedAmmo
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Speed = speed,
            Flags = flags,
            Value = value,
            ClipRounds = clipRounds,
            ProjectileFormId = projectileFormId,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedConsumable? ReconstructConsumableFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedConsumable
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        float weight = 0;
        uint value = 0;
        uint? addictionFormId = null;
        float addictionChance = 0;
        var effectFormIds = new List<uint>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 4:
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    break;
                case "ENIT" when sub.DataLength >= 16:
                    // ENIT layout: Value (S32 @0), Flags (U8 @4), unused (3),
                    //   Withdrawal Effect [SPEL] (@8), Addiction Chance (float @12),
                    //   Consume Sound [SOUN] (@16)
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    addictionFormId = ReadFormId(subData[8..], record.IsBigEndian);
                    addictionChance = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[12..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[12..]);
                    break;
                case "EFID" when sub.DataLength == 4:
                    effectFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedConsumable
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Weight = weight,
            Value = value,
            AddictionFormId = addictionFormId != 0 ? addictionFormId : null,
            AddictionChance = addictionChance,
            EffectFormIds = effectFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedMiscItem? ReconstructMiscItemFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedMiscItem
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        var value = 0;
        float weight = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 8:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
            }
        }

        return new ReconstructedMiscItem
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedPerk? ReconstructPerkFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedPerk
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? description = null;
        string? iconPath = null;
        byte trait = 0;
        byte minLevel = 0;
        byte ranks = 1;
        byte playable = 1;
        var entries = new List<PerkEntry>();

        // Track current entry being built
        byte currentEntryType = 0;
        byte currentEntryRank = 0;
        byte currentEntryPriority = 0;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DESC":
                    description = ReadNullTermString(subData);
                    break;
                case "ICON":
                case "MICO":
                    iconPath = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 5:
                    trait = subData[0];
                    minLevel = subData[1];
                    ranks = subData[2];
                    playable = subData[3];
                    break;
                case "PRKE" when sub.DataLength >= 3:
                    // Start new perk entry
                    currentEntryType = subData[0];
                    currentEntryRank = subData[1];
                    currentEntryPriority = subData[2];
                    break;
                case "EPFT" when sub.DataLength >= 1:
                    // Entry point function type - finalize entry
                    entries.Add(new PerkEntry
                    {
                        Type = currentEntryType,
                        Rank = currentEntryRank,
                        Priority = currentEntryPriority
                    });
                    break;
            }
        }

        return new ReconstructedPerk
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Description = description,
            IconPath = iconPath,
            Trait = trait,
            MinLevel = minLevel,
            Ranks = ranks,
            Playable = playable,
            Entries = entries,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedSpell? ReconstructSpellFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedSpell
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        SpellType type = 0;
        uint cost = 0;
        uint level = 0;
        byte flags = 0;
        var effectFormIds = new List<uint>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "SPIT" when sub.DataLength >= 16:
                    type = (SpellType)(record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData));
                    cost = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    level = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                    flags = subData[12];
                    break;
                case "EFID" when sub.DataLength == 4:
                    effectFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedSpell
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Type = type,
            Cost = cost,
            Level = level,
            Flags = flags,
            EffectFormIds = effectFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedRace? ReconstructRaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedRace
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        string? fullName = null;
        string? description = null;

        // S.P.E.C.I.A.L. modifiers
        sbyte strength = 0, perception = 0, endurance = 0, charisma = 0;
        sbyte intelligence = 0, agility = 0, luck = 0;

        // Heights
        var maleHeight = 1.0f;
        var femaleHeight = 1.0f;

        // Related FormIDs
        uint? olderRace = null;
        uint? youngerRace = null;
        uint? maleVoice = null;
        uint? femaleVoice = null;
        var abilityFormIds = new List<uint>();

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = ReadNullTermString(subData);
                    break;
                case "DESC":
                    description = ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 36:
                    // S.P.E.C.I.A.L. bonuses at offsets 0-6
                    strength = (sbyte)subData[0];
                    perception = (sbyte)subData[1];
                    endurance = (sbyte)subData[2];
                    charisma = (sbyte)subData[3];
                    intelligence = (sbyte)subData[4];
                    agility = (sbyte)subData[5];
                    luck = (sbyte)subData[6];
                    // Heights at offsets 20-27
                    maleHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[20..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[20..]);
                    femaleHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[24..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[24..]);
                    break;
                case "ONAM" when sub.DataLength == 4:
                    olderRace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "YNAM" when sub.DataLength == 4:
                    youngerRace = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "VTCK" when sub.DataLength >= 8:
                    maleVoice = ReadFormId(subData[..4], record.IsBigEndian);
                    femaleVoice = ReadFormId(subData[4..], record.IsBigEndian);
                    break;
                case "SPLO" when sub.DataLength == 4:
                    abilityFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedRace
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Description = description,
            Strength = strength,
            Perception = perception,
            Endurance = endurance,
            Charisma = charisma,
            Intelligence = intelligence,
            Agility = agility,
            Luck = luck,
            MaleHeight = maleHeight,
            FemaleHeight = femaleHeight,
            OlderRaceFormId = olderRace != 0 ? olderRace : null,
            YoungerRaceFormId = youngerRace != 0 ? youngerRace : null,
            MaleVoiceFormId = maleVoice != 0 ? maleVoice : null,
            FemaleVoiceFormId = femaleVoice != 0 ? femaleVoice : null,
            AbilityFormIds = abilityFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Helper Methods

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
    ///     Find FULL subrecord by parsing the record's data using the accessor.
    ///     Only finds FULL subrecords within the record's own data bounds.
    /// </summary>
    private string? FindFullNameInRecord(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24; // Skip main record header
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return FindFullNameInRecordBounds(record);
        }

        Array.Clear(buffer, 0, dataSize);
        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            if (sub.Signature == "FULL")
            {
                return ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
            }
        }

        return null;
    }

    /// <summary>
    ///     Find a 4-byte FormID subrecord within a record's data bounds.
    ///     Used for TNAM (speaker) and similar FormID-only subrecords.
    /// </summary>
    private uint? FindFormIdSubrecordInRecord(DetectedMainRecord record, byte[] buffer, string signature)
    {
        var dataStart = record.Offset + 24; // Skip main record header
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return null;
        }

        Array.Clear(buffer, 0, dataSize);
        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            if (sub.Signature == signature && sub.DataLength == 4)
            {
                return record.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset, 4));
            }
        }

        return null;
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

    private string? FindFullNameNear(long recordOffset)
    {
        return _scanResult.FullNames
            .Where(f => Math.Abs(f.Offset - recordOffset) < 500)
            .OrderBy(f => Math.Abs(f.Offset - recordOffset))
            .FirstOrDefault()?.Text;
    }

    private ActorBaseSubrecord? FindActorBaseNear(long recordOffset)
    {
        return _scanResult.ActorBases
            .Where(a => Math.Abs(a.Offset - recordOffset) < 500)
            .OrderBy(a => Math.Abs(a.Offset - recordOffset))
            .FirstOrDefault();
    }

    private static ActorBaseSubrecord? ParseActorBase(ReadOnlySpan<byte> data, long offset, bool bigEndian)
    {
        if (data.Length < 24)
        {
            return null;
        }

        uint flags;
        ushort fatigueBase, barterGold, calcMin, calcMax, speedMultiplier, templateFlags;
        short level, dispositionBase;
        float karmaAlignment;

        if (bigEndian)
        {
            flags = BinaryPrimitives.ReadUInt32BigEndian(data);
            fatigueBase = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            barterGold = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
            level = BinaryPrimitives.ReadInt16BigEndian(data[8..]);
            calcMin = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
            calcMax = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
            speedMultiplier = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
            karmaAlignment = BinaryPrimitives.ReadSingleBigEndian(data[16..]);
            dispositionBase = BinaryPrimitives.ReadInt16BigEndian(data[20..]);
            templateFlags = BinaryPrimitives.ReadUInt16BigEndian(data[22..]);
        }
        else
        {
            flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
            fatigueBase = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            barterGold = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            level = BinaryPrimitives.ReadInt16LittleEndian(data[8..]);
            calcMin = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
            calcMax = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
            speedMultiplier = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
            karmaAlignment = BinaryPrimitives.ReadSingleLittleEndian(data[16..]);
            dispositionBase = BinaryPrimitives.ReadInt16LittleEndian(data[20..]);
            templateFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
        }

        return new ActorBaseSubrecord(
            flags, fatigueBase, barterGold, level, calcMin, calcMax,
            speedMultiplier, karmaAlignment, dispositionBase, templateFlags,
            offset, bigEndian);
    }

    private static uint ReadFormId(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 4)
        {
            return 0;
        }

        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private static string ReadNullTermString(ReadOnlySpan<byte> data)
    {
        var length = data.IndexOf((byte)0);
        if (length < 0)
        {
            length = data.Length;
        }

        return Encoding.UTF8.GetString(data[..length]);
    }

    private readonly record struct ParsedSubrecordInfo(string Signature, int DataOffset, int DataLength);

    private static IEnumerable<ParsedSubrecordInfo> IterateSubrecords(byte[] data, int dataSize, bool bigEndian)
    {
        var offset = 0;

        while (offset + 6 <= dataSize)
        {
            // Read subrecord signature (4 bytes)
            var sig = bigEndian
                ? new string([
                    (char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]
                ])
                : Encoding.ASCII.GetString(data, offset, 4);

            // Read subrecord size (2 bytes)
            var subSize = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));

            if (offset + 6 + subSize > dataSize)
            {
                yield break;
            }

            yield return new ParsedSubrecordInfo(sig, offset + 6, subSize);

            offset += 6 + subSize;
        }
    }

    /// <summary>
    ///     Reconstruct all Game Setting (GMST) records from the scan result.
    /// </summary>
    public List<ReconstructedGameSetting> ReconstructGameSettings()
    {
        var settings = new List<ReconstructedGameSetting>();
        var gmstRecords = GetRecordsByType("GMST").ToList();

        if (_accessor == null)
        {
            // Without accessor, just return basic info
            foreach (var record in gmstRecords)
            {
                settings.Add(new ReconstructedGameSetting
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return settings;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(512);
        try
        {
            foreach (var record in gmstRecords)
            {
                var setting = ReconstructGameSettingFromAccessor(record, buffer);
                if (setting != null)
                {
                    settings.Add(setting);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return settings;
    }

    private ReconstructedGameSetting? ReconstructGameSettingFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedGameSetting
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        Array.Clear(buffer, 0, dataSize);
        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        byte[]? dataValue = null;

        foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        _formIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "DATA":
                    dataValue = new byte[sub.DataLength];
                    Array.Copy(buffer, sub.DataOffset, dataValue, 0, sub.DataLength);
                    break;
            }
        }

        // Determine type from first letter of EditorId
        var valueType = GameSettingType.Integer;
        float? floatValue = null;
        int? intValue = null;
        string? stringValue = null;

        if (!string.IsNullOrEmpty(editorId) && dataValue != null)
        {
            var typeChar = char.ToLowerInvariant(editorId[0]);
            switch (typeChar)
            {
                case 'f' when dataValue.Length >= 4:
                    valueType = GameSettingType.Float;
                    floatValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(dataValue)
                        : BinaryPrimitives.ReadSingleLittleEndian(dataValue);
                    break;
                case 'i' when dataValue.Length >= 4:
                    valueType = GameSettingType.Integer;
                    intValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(dataValue)
                        : BinaryPrimitives.ReadInt32LittleEndian(dataValue);
                    break;
                case 'b' when dataValue.Length >= 4:
                    valueType = GameSettingType.Boolean;
                    intValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(dataValue)
                        : BinaryPrimitives.ReadInt32LittleEndian(dataValue);
                    break;
                case 's':
                    valueType = GameSettingType.String;
                    stringValue = ReadNullTermString(dataValue);
                    break;
            }
        }

        return new ReconstructedGameSetting
        {
            FormId = record.FormId,
            EditorId = editorId,
            ValueType = valueType,
            FloatValue = floatValue,
            IntValue = intValue,
            StringValue = stringValue,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region New Record Types

    /// <summary>
    ///     Reconstruct all Global Variable (GLOB) records.
    /// </summary>
    public List<ReconstructedGlobal> ReconstructGlobals()
    {
        var globals = new List<ReconstructedGlobal>();

        if (_accessor == null)
        {
            return globals;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in GetRecordsByType("GLOB"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null;
                var valueType = 'f';
                float value = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FNAM" when sub.DataLength >= 1:
                            valueType = (char)buffer[sub.DataOffset];
                            break;
                        case "FLTV" when sub.DataLength >= 4:
                            value = record.IsBigEndian
                                ? BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                    }
                }

                globals.Add(new ReconstructedGlobal
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    ValueType = valueType,
                    Value = value,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return globals;
    }

    /// <summary>
    ///     Reconstruct all Enchantment (ENCH) records.
    /// </summary>
    public List<ReconstructedEnchantment> ReconstructEnchantments()
    {
        var enchantments = new List<ReconstructedEnchantment>();

        if (_accessor == null)
        {
            return enchantments;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("ENCH"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null;
                string? fullName = null;
                uint enchantType = 0, chargeAmount = 0, enchantCost = 0;
                byte flags = 0;
                var effects = new List<EnchantmentEffect>();
                uint currentEffectId = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ENIT" when sub.DataLength >= 12:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                enchantType = BinaryPrimitives.ReadUInt32BigEndian(span);
                                chargeAmount = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                enchantCost = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                            }
                            else
                            {
                                enchantType = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                chargeAmount = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                enchantCost = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                            }

                            if (sub.DataLength >= 13)
                            {
                                flags = buffer[sub.DataOffset + 12];
                            }

                            break;
                        }
                        case "EFID" when sub.DataLength >= 4:
                            currentEffectId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "EFIT" when sub.DataLength >= 12:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            float magnitude;
                            uint area, duration, type;
                            int actorValue;
                            if (record.IsBigEndian)
                            {
                                magnitude = BinaryPrimitives.ReadSingleBigEndian(span);
                                area = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                duration = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                type = sub.DataLength >= 16 ? BinaryPrimitives.ReadUInt32BigEndian(span[12..]) : 0;
                                actorValue = sub.DataLength >= 20
                                    ? BinaryPrimitives.ReadInt32BigEndian(span[16..])
                                    : -1;
                            }
                            else
                            {
                                magnitude = BinaryPrimitives.ReadSingleLittleEndian(span);
                                area = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                duration = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                type = sub.DataLength >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(span[12..]) : 0;
                                actorValue = sub.DataLength >= 20
                                    ? BinaryPrimitives.ReadInt32LittleEndian(span[16..])
                                    : -1;
                            }

                            effects.Add(new EnchantmentEffect
                            {
                                EffectFormId = currentEffectId,
                                Magnitude = magnitude,
                                Area = area,
                                Duration = duration,
                                Type = type,
                                ActorValue = actorValue
                            });
                            break;
                        }
                    }
                }

                enchantments.Add(new ReconstructedEnchantment
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    EnchantType = enchantType,
                    ChargeAmount = chargeAmount,
                    EnchantCost = enchantCost,
                    Flags = flags,
                    Effects = effects,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return enchantments;
    }

    /// <summary>
    ///     Reconstruct all Base Effect (MGEF) records.
    /// </summary>
    public List<ReconstructedBaseEffect> ReconstructBaseEffects()
    {
        var effects = new List<ReconstructedBaseEffect>();

        if (_accessor == null)
        {
            return effects;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in GetRecordsByType("MGEF"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null;
                string? icon = null, modelPath = null;
                uint flags = 0, associatedItem = 0, archetype = 0, projectile = 0, explosion = 0;
                float baseCost = 0;
                int magicSchool = -1, resistValue = -1, actorValue = -1;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 36:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                flags = BinaryPrimitives.ReadUInt32BigEndian(span);
                                baseCost = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                associatedItem = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                magicSchool = BinaryPrimitives.ReadInt32BigEndian(span[12..]);
                                resistValue = BinaryPrimitives.ReadInt32BigEndian(span[16..]);
                                archetype = BinaryPrimitives.ReadUInt32BigEndian(span[24..]);
                                actorValue = BinaryPrimitives.ReadInt32BigEndian(span[28..]);
                                if (sub.DataLength >= 44)
                                {
                                    projectile = BinaryPrimitives.ReadUInt32BigEndian(span[36..]);
                                }

                                if (sub.DataLength >= 48)
                                {
                                    explosion = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
                                }
                            }
                            else
                            {
                                flags = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                baseCost = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                associatedItem = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                magicSchool = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
                                resistValue = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
                                archetype = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
                                actorValue = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
                                if (sub.DataLength >= 44)
                                {
                                    projectile = BinaryPrimitives.ReadUInt32LittleEndian(span[36..]);
                                }

                                if (sub.DataLength >= 48)
                                {
                                    explosion = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                                }
                            }

                            break;
                        }
                    }
                }

                effects.Add(new ReconstructedBaseEffect
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Flags = flags,
                    BaseCost = baseCost,
                    AssociatedItem = associatedItem,
                    MagicSchool = magicSchool,
                    ResistValue = resistValue,
                    Archetype = archetype,
                    ActorValue = actorValue,
                    Projectile = projectile,
                    Explosion = explosion,
                    Icon = icon,
                    ModelPath = modelPath,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return effects;
    }

    /// <summary>
    ///     Reconstruct all Weapon Mod (IMOD) records.
    /// </summary>
    public List<ReconstructedWeaponMod> ReconstructWeaponMods()
    {
        var mods = new List<ReconstructedWeaponMod>();

        if (_accessor == null)
        {
            return mods;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in GetRecordsByType("IMOD"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null;
                string? modelPath = null, icon = null;
                var value = 0;
                float weight = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 8:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                value = BinaryPrimitives.ReadInt32BigEndian(span);
                                weight = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                            }
                            else
                            {
                                value = BinaryPrimitives.ReadInt32LittleEndian(span);
                                weight = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                            }

                            break;
                        }
                    }
                }

                mods.Add(new ReconstructedWeaponMod
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    ModelPath = modelPath,
                    Icon = icon,
                    Value = value,
                    Weight = weight,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return mods;
    }

    /// <summary>
    ///     Reconstruct all Recipe (RCPE) records.
    /// </summary>
    public List<ReconstructedRecipe> ReconstructRecipes()
    {
        var recipes = new List<ReconstructedRecipe>();

        if (_accessor == null)
        {
            return recipes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("RCPE"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null;
                int requiredSkill = -1, requiredSkillLevel = 0;
                uint categoryFormId = 0, subcategoryFormId = 0;
                var ingredients = new List<RecipeIngredient>();
                var outputs = new List<RecipeOutput>();

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 16:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                requiredSkill = BinaryPrimitives.ReadInt32BigEndian(span);
                                requiredSkillLevel = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
                                categoryFormId = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                subcategoryFormId = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                            }
                            else
                            {
                                requiredSkill = BinaryPrimitives.ReadInt32LittleEndian(span);
                                requiredSkillLevel = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
                                categoryFormId = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                subcategoryFormId = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                            }

                            break;
                        }
                        case "RCIL" when sub.DataLength >= 8:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            var itemId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(span)
                                : BinaryPrimitives.ReadUInt32LittleEndian(span);
                            var count = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(span[4..])
                                : BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                            ingredients.Add(new RecipeIngredient { ItemFormId = itemId, Count = count });
                            break;
                        }
                        case "RCOD" when sub.DataLength >= 8:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            var itemId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(span)
                                : BinaryPrimitives.ReadUInt32LittleEndian(span);
                            var count = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(span[4..])
                                : BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                            outputs.Add(new RecipeOutput { ItemFormId = itemId, Count = count });
                            break;
                        }
                    }
                }

                recipes.Add(new ReconstructedRecipe
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    RequiredSkill = requiredSkill,
                    RequiredSkillLevel = requiredSkillLevel,
                    CategoryFormId = categoryFormId,
                    SubcategoryFormId = subcategoryFormId,
                    Ingredients = ingredients,
                    Outputs = outputs,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return recipes;
    }

    /// <summary>
    ///     Reconstruct all Challenge (CHAL) records.
    /// </summary>
    public List<ReconstructedChallenge> ReconstructChallenges()
    {
        var challenges = new List<ReconstructedChallenge>();

        if (_accessor == null)
        {
            return challenges;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("CHAL"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null, icon = null;
                uint challengeType = 0, threshold = 0, flags = 0, interval = 0;
                uint value1 = 0, value2 = 0, value3 = 0, script = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "SCRI" when sub.DataLength >= 4:
                            script = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "DATA" when sub.DataLength >= 20:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                challengeType = BinaryPrimitives.ReadUInt32BigEndian(span);
                                threshold = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                flags = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                interval = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                                value1 = sub.DataLength >= 24 ? BinaryPrimitives.ReadUInt32BigEndian(span[16..]) : 0;
                                value2 = sub.DataLength >= 28 ? BinaryPrimitives.ReadUInt32BigEndian(span[20..]) : 0;
                                value3 = sub.DataLength >= 32 ? BinaryPrimitives.ReadUInt32BigEndian(span[24..]) : 0;
                            }
                            else
                            {
                                challengeType = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                threshold = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                flags = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                interval = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                                value1 = sub.DataLength >= 24 ? BinaryPrimitives.ReadUInt32LittleEndian(span[16..]) : 0;
                                value2 = sub.DataLength >= 28 ? BinaryPrimitives.ReadUInt32LittleEndian(span[20..]) : 0;
                                value3 = sub.DataLength >= 32 ? BinaryPrimitives.ReadUInt32LittleEndian(span[24..]) : 0;
                            }

                            break;
                        }
                    }
                }

                challenges.Add(new ReconstructedChallenge
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    ChallengeType = challengeType,
                    Threshold = threshold,
                    Flags = flags,
                    Interval = interval,
                    Value1 = value1,
                    Value2 = value2,
                    Value3 = value3,
                    Script = script,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return challenges;
    }

    /// <summary>
    ///     Reconstruct all Reputation (REPU) records.
    /// </summary>
    public List<ReconstructedReputation> ReconstructReputations()
    {
        var reputations = new List<ReconstructedReputation>();

        if (_accessor == null)
        {
            return reputations;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in GetRecordsByType("REPU"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null;
                float positiveValue = 0, negativeValue = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 8:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                positiveValue = BinaryPrimitives.ReadSingleBigEndian(span);
                                negativeValue = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                            }
                            else
                            {
                                positiveValue = BinaryPrimitives.ReadSingleLittleEndian(span);
                                negativeValue = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                            }

                            break;
                        }
                    }
                }

                reputations.Add(new ReconstructedReputation
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    PositiveValue = positiveValue,
                    NegativeValue = negativeValue,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return reputations;
    }

    /// <summary>
    ///     Reconstruct all Projectile (PROJ) records.
    /// </summary>
    public List<ReconstructedProjectile> ReconstructProjectiles()
    {
        var projectiles = new List<ReconstructedProjectile>();

        if (_accessor == null)
        {
            return projectiles;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("PROJ"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, modelPath = null;
                ushort projFlags = 0, projType = 0;
                float gravity = 0, speed = 0, range = 0;
                float muzzleFlashDuration = 0, fadeDuration = 0, impactForce = 0, timer = 0;
                uint light = 0, muzzleFlashLight = 0, explosion = 0, sound = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 52:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                projFlags = BinaryPrimitives.ReadUInt16BigEndian(span);
                                projType = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
                                gravity = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                speed = BinaryPrimitives.ReadSingleBigEndian(span[8..]);
                                range = BinaryPrimitives.ReadSingleBigEndian(span[12..]);
                                light = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
                                muzzleFlashLight = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
                                muzzleFlashDuration = BinaryPrimitives.ReadSingleBigEndian(span[28..]);
                                fadeDuration = BinaryPrimitives.ReadSingleBigEndian(span[32..]);
                                impactForce = BinaryPrimitives.ReadSingleBigEndian(span[36..]);
                                sound = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
                                timer = BinaryPrimitives.ReadSingleBigEndian(span[48..]);
                                explosion = sub.DataLength >= 56 ? BinaryPrimitives.ReadUInt32BigEndian(span[52..]) : 0;
                            }
                            else
                            {
                                projFlags = BinaryPrimitives.ReadUInt16LittleEndian(span);
                                projType = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
                                gravity = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                speed = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
                                range = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
                                light = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
                                muzzleFlashLight = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
                                muzzleFlashDuration = BinaryPrimitives.ReadSingleLittleEndian(span[28..]);
                                fadeDuration = BinaryPrimitives.ReadSingleLittleEndian(span[32..]);
                                impactForce = BinaryPrimitives.ReadSingleLittleEndian(span[36..]);
                                sound = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                                timer = BinaryPrimitives.ReadSingleLittleEndian(span[48..]);
                                explosion = sub.DataLength >= 56
                                    ? BinaryPrimitives.ReadUInt32LittleEndian(span[52..])
                                    : 0;
                            }

                            break;
                        }
                    }
                }

                projectiles.Add(new ReconstructedProjectile
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    ModelPath = modelPath,
                    Flags = projFlags,
                    ProjectileType = projType,
                    Gravity = gravity,
                    Speed = speed,
                    Range = range,
                    Light = light,
                    MuzzleFlashLight = muzzleFlashLight,
                    Explosion = explosion,
                    Sound = sound,
                    MuzzleFlashDuration = muzzleFlashDuration,
                    FadeDuration = fadeDuration,
                    ImpactForce = impactForce,
                    Timer = timer,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return projectiles;
    }

    /// <summary>
    ///     Reconstruct all Explosion (EXPL) records.
    /// </summary>
    public List<ReconstructedExplosion> ReconstructExplosions()
    {
        var explosions = new List<ReconstructedExplosion>();

        if (_accessor == null)
        {
            return explosions;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("EXPL"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, modelPath = null;
                float force = 0, damage = 0, radius = 0, isRadius = 0;
                uint light = 0, sound1 = 0, flags = 0, impactDataSet = 0, sound2 = 0, enchantment = 0;

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "EITM" when sub.DataLength >= 4:
                            enchantment = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "DATA" when sub.DataLength >= 36:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            if (record.IsBigEndian)
                            {
                                force = BinaryPrimitives.ReadSingleBigEndian(span);
                                damage = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                radius = BinaryPrimitives.ReadSingleBigEndian(span[8..]);
                                light = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                                sound1 = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
                                flags = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
                                isRadius = BinaryPrimitives.ReadSingleBigEndian(span[24..]);
                                impactDataSet = BinaryPrimitives.ReadUInt32BigEndian(span[28..]);
                                sound2 = BinaryPrimitives.ReadUInt32BigEndian(span[32..]);
                            }
                            else
                            {
                                force = BinaryPrimitives.ReadSingleLittleEndian(span);
                                damage = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                radius = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
                                light = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                                sound1 = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
                                flags = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
                                isRadius = BinaryPrimitives.ReadSingleLittleEndian(span[24..]);
                                impactDataSet = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
                                sound2 = BinaryPrimitives.ReadUInt32LittleEndian(span[32..]);
                            }

                            break;
                        }
                    }
                }

                explosions.Add(new ReconstructedExplosion
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    ModelPath = modelPath,
                    Force = force,
                    Damage = damage,
                    Radius = radius,
                    Light = light,
                    Sound1 = sound1,
                    Flags = flags,
                    ISRadius = isRadius,
                    ImpactDataSet = impactDataSet,
                    Sound2 = sound2,
                    Enchantment = enchantment,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return explosions;
    }

    /// <summary>
    ///     Reconstruct all Message (MESG) records.
    /// </summary>
    public List<ReconstructedMessage> ReconstructMessages()
    {
        var messages = new List<ReconstructedMessage>();

        if (_accessor == null)
        {
            return messages;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("MESG"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null, icon = null;
                uint questFormId = 0, flags = 0, displayTime = 0;
                var buttons = new List<string>();

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "QNAM" when sub.DataLength >= 4:
                            questFormId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "DNAM" when sub.DataLength >= 4:
                            flags = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "TNAM" when sub.DataLength >= 4:
                            displayTime = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "ITXT":
                        {
                            var btnText = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(btnText))
                            {
                                buttons.Add(btnText);
                            }

                            break;
                        }
                    }
                }

                messages.Add(new ReconstructedMessage
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    QuestFormId = questFormId,
                    Flags = flags,
                    DisplayTime = displayTime,
                    Buttons = buttons,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return messages;
    }

    /// <summary>
    ///     Reconstruct all Class (CLAS) records.
    /// </summary>
    public List<ReconstructedClass> ReconstructClasses()
    {
        var classes = new List<ReconstructedClass>();

        if (_accessor == null)
        {
            return classes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in GetRecordsByType("CLAS"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null, icon = null;
                var tagSkills = new List<int>();
                uint classFlags = 0, barterFlags = 0;
                byte trainingSkill = 0, trainingLevel = 0;
                var attributeWeights = Array.Empty<byte>();

                foreach (var sub in IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 20:
                        {
                            var span = buffer.AsSpan(sub.DataOffset);
                            // DATA: 4 tag skill indices (int32 each) + flags (uint32) + barter flags (uint32)
                            for (var i = 0; i < 4 && i * 4 + 4 <= sub.DataLength - 8; i++)
                            {
                                var skill = record.IsBigEndian
                                    ? BinaryPrimitives.ReadInt32BigEndian(span[(i * 4)..])
                                    : BinaryPrimitives.ReadInt32LittleEndian(span[(i * 4)..]);
                                if (skill >= 0)
                                {
                                    tagSkills.Add(skill);
                                }
                            }

                            var flagsOffset = sub.DataLength - 8;
                            if (record.IsBigEndian)
                            {
                                classFlags = BinaryPrimitives.ReadUInt32BigEndian(span[flagsOffset..]);
                                barterFlags = BinaryPrimitives.ReadUInt32BigEndian(span[(flagsOffset + 4)..]);
                            }
                            else
                            {
                                classFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[flagsOffset..]);
                                barterFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[(flagsOffset + 4)..]);
                            }

                            break;
                        }
                        case "ATTR" when sub.DataLength >= 2:
                        {
                            trainingSkill = buffer[sub.DataOffset];
                            trainingLevel = buffer[sub.DataOffset + 1];
                            if (sub.DataLength >= 9)
                            {
                                attributeWeights = new byte[7];
                                Array.Copy(buffer, sub.DataOffset + 2, attributeWeights, 0, 7);
                            }

                            break;
                        }
                    }
                }

                classes.Add(new ReconstructedClass
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    TagSkills = tagSkills.ToArray(),
                    Flags = classFlags,
                    BarterFlags = barterFlags,
                    TrainingSkill = trainingSkill,
                    TrainingLevel = trainingLevel,
                    AttributeWeights = attributeWeights,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return classes;
    }

    #endregion
}

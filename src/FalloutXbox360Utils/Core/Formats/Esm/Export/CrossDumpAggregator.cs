using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Aggregates record data from multiple memory dumps for cross-dump comparison.
///     Processes dumps sequentially, formatting records and indexing by FormID.
/// </summary>
internal static class CrossDumpAggregator
{
    /// <summary>
    ///     Aggregate record data from multiple dumps into a cross-dump index.
    ///     Uses PE TimeDateStamp from game module for build dates when available.
    /// </summary>
    internal static CrossDumpRecordIndex Aggregate(
        List<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)> dumps,
        IReadOnlySet<string>? allowedTypes = null,
        bool releaseInputRecords = false,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>? npcPlacementIndexes = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>?
            npcScriptReferenceIndexes = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>?
            containerPlacementIndexes = null)
    {
        var index = new CrossDumpRecordIndex();

        // Sort by build date (PE timestamp) falling back to file date
        var ordered = dumps
            .Select(d =>
            {
                var fi = new FileInfo(d.FilePath);
                var fileDate = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;

                var isDmp = d.FilePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);

                // Use PE timestamp from game module if available
                var buildDate = fileDate;
                var dateSource = "file timestamp";
                if (d.Info != null)
                {
                    var gameModule = d.Info.FindGameModule();
                    if (gameModule != null && gameModule.TimeDateStamp != 0)
                    {
                        buildDate = DateTimeOffset.FromUnixTimeSeconds(gameModule.TimeDateStamp).UtcDateTime;
                        dateSource = "PE TimeDateStamp";
                    }
                }
                else if (!isDmp)
                {
                    var esmDate = EsmBuildDateExtractor.Extract(d.FilePath);
                    buildDate = esmDate.BuildDateUtc;
                    dateSource = esmDate.Source;
                }

                var shortName = Path.GetFileNameWithoutExtension(d.FilePath);
                return (d.FilePath, d.Records, d.Resolver, Date: buildDate, ShortName: shortName, IsDmp: isDmp,
                    DateSource: dateSource);
            })
            .OrderBy(d => d.Date)
            .ToList();
        if (releaseInputRecords)
        {
            dumps.Clear();
        }

        var virtualCellCanonicalFormIds = ShouldIncludeType(allowedTypes, "Cell") ||
                                          ShouldIncludeType(allowedTypes, "MapMarker") ||
                                          ShouldIncludeType(allowedTypes, "Key") ||
                                          ShouldIncludeType(allowedTypes, "Container")
            ? BuildVirtualCellCanonicalFormIds(ordered.Select(d => d.Records))
            : new Dictionary<CellCoordinateKey, RealCellCandidate>();
        var keyLockedDoorIndexes = ShouldIncludeType(allowedTypes, "Key")
            ? BuildKeyLockedDoorIndexes(
                ordered.Select(d => (d.FilePath, d.Records)),
                virtualCellCanonicalFormIds)
            : null;
        if (ShouldIncludeType(allowedTypes, "NPC") && npcPlacementIndexes == null)
        {
            npcPlacementIndexes = BuildNpcPlacementIndexes(
                ordered.Select(d => (d.FilePath, d.Records)));
        }

        if (ShouldIncludeType(allowedTypes, "NPC") && npcScriptReferenceIndexes == null)
        {
            npcScriptReferenceIndexes = BuildNpcScriptReferenceIndexes(
                ordered.Select(d => (d.FilePath, d.Records)));
        }

        if (ShouldIncludeType(allowedTypes, "Container") && containerPlacementIndexes == null)
        {
            containerPlacementIndexes = BuildContainerPlacementIndexes(
                ordered.Select(d => (d.FilePath, d.Records)),
                virtualCellCanonicalFormIds);
        }

        var upgradedVirtualCellIds = new Dictionary<uint, SortedSet<uint>>();
        var upgradedVirtualCellIdsByDump = new Dictionary<uint, SortedDictionary<int, SortedSet<uint>>>();

        // Canonical labels: FormID → display label.
        // Ensures all dialogue from the same NPC/quest shares one group label
        // even if the display name changed between builds.
        var speakerLabels = new Dictionary<uint, string>();
        var questLabels = new Dictionary<uint, string>();
        // Canonical worldspace labels: cells from the same worldspace should all use
        // the same label even if early DMPs resolve the worldspace name differently
        // than later ESMs (e.g., WastelandNVOLD vs WastelandNV for the same FormID).
        var worldspaceLabels = new Dictionary<uint, string>();
        // Track cells whose group was set by an ESM (authoritative). DMP cell records
        // may have a misread WorldspaceFormId from runtime struct parsing, so they
        // should not overwrite ESM-sourced grouping.
        var cellGroupFromEsm = new HashSet<uint>();

        for (var dumpIdx = 0; dumpIdx < ordered.Count; dumpIdx++)
        {
            var dump = ordered[dumpIdx];
            index.Dumps.Add(new DumpSnapshot(
                Path.GetFileName(dump.FilePath),
                dump.Date,
                dump.ShortName,
                dump.IsDmp,
                DateSource: dump.DateSource));

            // Build reverse indexes once per dump for enriched reports, but skip
            // them for scoped runs that cannot use those enrichments.
            var factionMembers = ShouldIncludeType(allowedTypes, "Faction")
                ? dump.Records.BuildFactionMembersIndex()
                : null;
            var keyLockedDoors = ShouldIncludeType(allowedTypes, "Key") &&
                                 keyLockedDoorIndexes != null &&
                                 keyLockedDoorIndexes.TryGetValue(dump.FilePath, out var keysForDump)
                ? keysForDump
                : null;
            var modToWeapon = ShouldIncludeType(allowedTypes, "WeaponMod")
                ? dump.Records.BuildModToWeaponMap()
                : null;
            var placedReferenceLocations = ShouldIncludeType(allowedTypes, "Cell") ||
                                           ShouldIncludeType(allowedTypes, "MapMarker")
                ? BuildPlacedReferenceLocations(dump.Records.Cells, virtualCellCanonicalFormIds)
                : null;
            var npcPlacements = ShouldIncludeType(allowedTypes, "NPC") &&
                                npcPlacementIndexes != null &&
                                npcPlacementIndexes.TryGetValue(dump.FilePath, out var placementsForDump)
                ? placementsForDump
                : null;
            var npcScriptReferences = ShouldIncludeType(allowedTypes, "NPC") &&
                                      npcScriptReferenceIndexes != null &&
                                      npcScriptReferenceIndexes.TryGetValue(dump.FilePath, out var refsForDump)
                ? refsForDump
                : null;
            var containerPlacements = ShouldIncludeType(allowedTypes, "Container") &&
                                      containerPlacementIndexes != null &&
                                      containerPlacementIndexes.TryGetValue(dump.FilePath, out var containersForDump)
                ? containersForDump
                : null;
            var dialogTopicsByFormId = ShouldIncludeType(allowedTypes, "Dialogue")
                ? BuildDialogTopicLookup(dump.Records.DialogTopics)
                : null;
            var dialogTopicSearchTextByFormId = ShouldIncludeType(allowedTypes, "DialogTopic")
                ? BuildDialogTopicSearchTextLookup(dump.Records.Dialogues)
                : null;

            foreach (var (typeName, formId, _, _, record) in
                     RecordTextFormatter.EnumerateAll(dump.Records))
            {
                if (!ShouldIncludeType(allowedTypes, typeName))
                {
                    continue;
                }

                if (record is CellRecord { IsUnresolvedBucket: true })
                {
                    continue;
                }

                var reportFormId = formId;
                var reportWasRebasedVirtualCell = false;
                var canonicalCell = default(RealCellCandidate);
                if (record is CellRecord identityCell &&
                    TryGetVirtualCellCanonicalFormId(
                        identityCell,
                        virtualCellCanonicalFormIds,
                        out canonicalCell))
                {
                    reportFormId = canonicalCell.FormId;
                    reportWasRebasedVirtualCell = true;
                    if (!upgradedVirtualCellIds.TryGetValue(reportFormId, out var originalIds))
                    {
                        originalIds = [];
                        upgradedVirtualCellIds[reportFormId] = originalIds;
                    }

                    originalIds.Add(formId);
                    AddUpgradedVirtualCellForDump(upgradedVirtualCellIdsByDump, reportFormId, dumpIdx, formId);
                }

                // Build structured report (primary path for all output formats)
                var report = RecordTextFormatter.BuildReport(record, dump.Resolver,
                    factionMembers, keyLockedDoors, modToWeapon, placedReferenceLocations, npcPlacements,
                    npcScriptReferences, containerPlacements);
                if (report == null) continue;
                if (reportWasRebasedVirtualCell)
                {
                    report = RebaseVirtualCellReport(report, canonicalCell);
                }

                if (!index.StructuredRecords.TryGetValue(typeName, out var structFormIdMap))
                {
                    structFormIdMap = new Dictionary<uint, Dictionary<int, RecordReport>>();
                    index.StructuredRecords[typeName] = structFormIdMap;
                }

                if (!structFormIdMap.TryGetValue(reportFormId, out var structDumpMap))
                {
                    structDumpMap = new Dictionary<int, RecordReport>();
                    structFormIdMap[reportFormId] = structDumpMap;
                }

                if (record is CellRecord { IsVirtual: true } && structDumpMap.ContainsKey(dumpIdx))
                {
                    continue;
                }

                structDumpMap[dumpIdx] = report;

                // Compute group key and store grid coords for cells
                if (record is CellRecord c)
                {
                    if (!index.RecordGroups.TryGetValue(typeName, out var gm))
                    {
                        gm = new Dictionary<uint, string>();
                        index.RecordGroups[typeName] = gm;
                    }

                    // Group key is based on worldspace FormID (or "Interior" / "Unknown").
                    // Use placeholder "WS:0xFORMID" during aggregation, then replace with
                    // resolved names in a finalization pass. Track the best name per FormID.
                    string newGroupKey;
                    if (c.IsInterior)
                    {
                        newGroupKey = "Interior Cells";
                    }
                    else if (c.WorldspaceFormId.HasValue)
                    {
                        var wsFid = c.WorldspaceFormId.Value;
                        newGroupKey = $"WS:0x{wsFid:X8}";

                        // Track the best display label for this worldspace FormID.
                        // Prefer display names over EditorIDs; later dumps overwrite earlier.
                        // Filter out resolver placeholder strings like "(none)".
                        var wsDisplayName = dump.Resolver.ResolveDisplayName(wsFid);
                        var wsEditorId = dump.Resolver.ResolveEditorId(wsFid);
                        var bestName = PickRealName(wsDisplayName, wsEditorId);
                        if (bestName != null)
                        {
                            // Only overwrite if we don't already have a real name,
                            // or if the new name is also real (let later dumps win)
                            worldspaceLabels[wsFid] = bestName;
                        }
                    }
                    else
                    {
                        newGroupKey = "Exterior Cells (Unknown Worldspace)";
                    }

                    if (!gm.TryGetValue(reportFormId, out var existingGroup))
                    {
                        gm[reportFormId] = newGroupKey;
                        if (!dump.IsDmp) cellGroupFromEsm.Add(reportFormId);
                    }
                    else if (!dump.IsDmp)
                    {
                        // ESMs are authoritative — overwrite any DMP-sourced group.
                        // (DMP cell parser may misread WorldspaceFormId from runtime structs.)
                        gm[reportFormId] = newGroupKey;
                        cellGroupFromEsm.Add(reportFormId);
                    }
                    else if (!cellGroupFromEsm.Contains(reportFormId))
                    {
                        // DMP→DMP upgrade: only allow Interior or Unknown→worldspace
                        if (c.IsInterior && existingGroup != "Interior Cells")
                        {
                            gm[reportFormId] = "Interior Cells";
                        }
                        else if (existingGroup == "Exterior Cells (Unknown Worldspace)"
                                 && c.WorldspaceFormId.HasValue)
                        {
                            gm[reportFormId] = newGroupKey;
                        }
                    }

                    // Store grid coordinates for CSS grid tile map (latest wins —
                    // ESM coords are authoritative over DMP-inferred coords)
                    if (c.GridX.HasValue && c.GridY.HasValue)
                    {
                        index.CellGridCoords[reportFormId] = (c.GridX.Value, c.GridY.Value);
                    }

                    // Heightmap storage is handled separately from ESM LAND records
                    // (see DmpCompareCommand after Aggregate call) for complete coverage.
                }

                // Compute dialogue groups (by quest and by NPC) and per-record metadata
                if (record is DialogueRecord d)
                {
                    // Quest groups
                    if (!index.RecordGroups.TryGetValue("Dialogue_Quest", out var questGroups))
                    {
                        questGroups = new Dictionary<uint, string>();
                        index.RecordGroups["Dialogue_Quest"] = questGroups;
                    }

                    if (!questGroups.TryGetValue(formId, out var existingQuestGroup))
                    {
                        questGroups[formId] = d.QuestFormId.HasValue
                            ? ResolveQuestGroupLabel(d.QuestFormId.Value, dump.Resolver, questLabels)
                            : "(No Quest)";
                    }
                    else if (existingQuestGroup == "(No Quest)" && d.QuestFormId.HasValue)
                    {
                        questGroups[formId] =
                            ResolveQuestGroupLabel(d.QuestFormId.Value, dump.Resolver, questLabels);
                    }
                    else if (d.QuestFormId.HasValue)
                    {
                        // Update canonical label with better name resolution from later dumps
                        ResolveQuestGroupLabel(d.QuestFormId.Value, dump.Resolver, questLabels);
                    }

                    // NPC groups — use a speaker FormID-keyed canonical name so all
                    // dialogue lines from the same NPC share a single group label
                    // even if the NPC's display name changed between builds.
                    if (!index.RecordGroups.TryGetValue("Dialogue_NPC", out var npcGroups))
                    {
                        npcGroups = new Dictionary<uint, string>();
                        index.RecordGroups["Dialogue_NPC"] = npcGroups;
                    }

                    // Insert on first sighting; upgrade "(No Speaker)" if a later dump
                    // provides speaker or quest attribution.
                    var hasExistingNpcGroup = npcGroups.TryGetValue(formId, out var existingNpcGroup);
                    var canUpgradeNoSpeaker = hasExistingNpcGroup
                        && existingNpcGroup == "(No Speaker)"
                        && (d.SpeakerFormId.HasValue || d.QuestFormId.HasValue);
                    if (!hasExistingNpcGroup || canUpgradeNoSpeaker)
                    {
                        npcGroups[formId] = ResolveSpeakerGroupLabel(d, dump.Resolver, speakerLabels);
                    }

                    // Per-record metadata for table columns — update on each dump
                    // so later dumps can fill in missing quest/topic/speaker info
                    if (!index.RecordMetadata.TryGetValue(typeName, out var metaMap))
                    {
                        metaMap = new Dictionary<uint, Dictionary<string, string>>();
                        index.RecordMetadata[typeName] = metaMap;
                    }

                    if (!metaMap.TryGetValue(formId, out var meta))
                    {
                        meta = new Dictionary<string, string>();
                        metaMap[formId] = meta;
                    }

                    // Update metadata from each dump — latest wins for best name resolution.
                    // Also tracks name changes across builds for history display.
                    if (d.QuestFormId.HasValue)
                    {
                        meta["questFormId"] = $"0x{d.QuestFormId.Value:X8}";
                        var qEid = dump.Resolver.GetEditorId(d.QuestFormId.Value) ?? "";
                        var qName = dump.Resolver.GetDisplayName(d.QuestFormId.Value) ?? "";
                        if (!string.IsNullOrEmpty(qEid)) meta["questEditorId"] = qEid;
                        if (!string.IsNullOrEmpty(qName)) meta["questName"] = qName;
                    }

                    if (d.TopicFormId.HasValue)
                    {
                        meta["topicFormId"] = $"0x{d.TopicFormId.Value:X8}";
                        var tEid = dump.Resolver.GetEditorId(d.TopicFormId.Value) ?? "";
                        var tName = dump.Resolver.GetDisplayName(d.TopicFormId.Value) ?? "";
                        if (string.IsNullOrEmpty(tName) &&
                            dialogTopicsByFormId != null &&
                            dialogTopicsByFormId.TryGetValue(d.TopicFormId.Value, out var topic))
                        {
                            tName = topic.FullName ?? topic.DummyPrompt ?? "";
                        }

                        if (string.IsNullOrEmpty(tName))
                        {
                            tName = PickFirstDialogueText(d);
                        }

                        if (!string.IsNullOrEmpty(tEid)) meta["topicEditorId"] = tEid;
                        if (!string.IsNullOrEmpty(tName)) meta["topicName"] = tName;
                    }

                    if (d.SpeakerFormId.HasValue)
                    {
                        meta["speakerFormId"] = $"0x{d.SpeakerFormId.Value:X8}";
                        var sEid = dump.Resolver.GetEditorId(d.SpeakerFormId.Value) ?? "";
                        var sName = dump.Resolver.GetDisplayName(d.SpeakerFormId.Value) ?? "";
                        if (!string.IsNullOrEmpty(sEid)) meta["speakerEditorId"] = sEid;
                        if (!string.IsNullOrEmpty(sName)) meta["speakerName"] = sName;
                    }
                }

                if (record is DialogTopicRecord dialogTopic &&
                    dialogTopicSearchTextByFormId != null &&
                    dialogTopicSearchTextByFormId.TryGetValue(dialogTopic.FormId, out var topicSearchText) &&
                    !string.IsNullOrWhiteSpace(topicSearchText))
                {
                    if (!index.RecordMetadata.TryGetValue(typeName, out var metaMap))
                    {
                        metaMap = new Dictionary<uint, Dictionary<string, string>>();
                        index.RecordMetadata[typeName] = metaMap;
                    }

                    if (!metaMap.TryGetValue(formId, out var meta))
                    {
                        meta = new Dictionary<string, string>();
                        metaMap[formId] = meta;
                    }

                    meta["searchText"] = topicSearchText;
                }
            }

            if (releaseInputRecords)
            {
                ClearRecordLists(dump.Records);
            }
        }

        AppendVirtualCellAuditMetadata(index, upgradedVirtualCellIds, upgradedVirtualCellIdsByDump);

        // Finalize: replace cell group placeholder keys ("WS:0xFORMID") with the
        // best-resolved worldspace name. All cells from the same worldspace FormID
        // will end up with the same group label, regardless of which dump's resolver
        // first saw them. Falls back to "Worldspace 0xFORMID" if no name was resolved.
        if (index.RecordGroups.TryGetValue("Cell", out var cellGroups))
        {
            foreach (var (cellFormId, currentKey) in cellGroups.ToList())
            {
                if (currentKey.StartsWith("WS:0x", StringComparison.Ordinal))
                {
                    var hex = currentKey[5..];
                    if (uint.TryParse(hex, NumberStyles.HexNumber, null, out var wsFid))
                    {
                        cellGroups[cellFormId] = worldspaceLabels.TryGetValue(wsFid, out var label)
                            ? label
                            : $"Worldspace 0x{wsFid:X8}";
                    }
                }
            }
        }

        // Finalize: sync all dialogue group labels to their canonical versions.
        // Speaker/quest labels may have been upgraded by later dumps, but earlier
        // dialogue records still reference the old label string.
        if (index.RecordGroups.TryGetValue("Dialogue_NPC", out var finalNpcGroups))
        {
            // Build reverse map: old label → canonical label
            var speakerFormIdToLabel = speakerLabels;
            foreach (var (dialogueFormId, currentLabel) in finalNpcGroups.ToList())
            {
                // Find the speaker FormID for this dialogue line from metadata
                if (index.RecordMetadata.TryGetValue("Dialogue", out var metas)
                    && metas.TryGetValue(dialogueFormId, out var m)
                    && m.TryGetValue("speakerFormId", out var spkHex)
                    && uint.TryParse(spkHex.Replace("0x", ""), NumberStyles.HexNumber,
                        null, out var spkFid)
                    && speakerFormIdToLabel.TryGetValue(spkFid, out var canonicalLabel)
                    && canonicalLabel != currentLabel)
                {
                    finalNpcGroups[dialogueFormId] = canonicalLabel;
                }
            }
        }

        if (index.RecordGroups.TryGetValue("Dialogue_Quest", out var finalQuestGroups))
        {
            foreach (var (dialogueFormId, currentLabel) in finalQuestGroups.ToList())
            {
                if (index.RecordMetadata.TryGetValue("Dialogue", out var metas)
                    && metas.TryGetValue(dialogueFormId, out var m)
                    && m.TryGetValue("questFormId", out var qstHex)
                    && uint.TryParse(qstHex.Replace("0x", ""), NumberStyles.HexNumber,
                        null, out var qstFid)
                    && questLabels.TryGetValue(qstFid, out var canonicalLabel)
                    && canonicalLabel != currentLabel)
                {
                    finalQuestGroups[dialogueFormId] = canonicalLabel;
                }
            }
        }

        return index;
    }

    private static bool ShouldIncludeType(IReadOnlySet<string>? allowedTypes, string typeName)
    {
        return allowedTypes is not { Count: > 0 } || allowedTypes.Contains(typeName);
    }

    private static Dictionary<uint, DialogTopicRecord> BuildDialogTopicLookup(
        IEnumerable<DialogTopicRecord> topics)
    {
        var map = new Dictionary<uint, DialogTopicRecord>();
        foreach (var topic in topics)
        {
            map.TryAdd(topic.FormId, topic);
        }

        return map;
    }

    private static Dictionary<uint, string> BuildDialogTopicSearchTextLookup(
        IEnumerable<DialogueRecord> dialogues)
    {
        var map = new Dictionary<uint, HashSet<string>>();
        foreach (var dialogue in dialogues)
        {
            if (dialogue.TopicFormId is not > 0)
            {
                continue;
            }

            if (!map.TryGetValue(dialogue.TopicFormId.Value, out var values))
            {
                values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map[dialogue.TopicFormId.Value] = values;
            }

            AddSearchValue(values, dialogue.PromptText);
            foreach (var response in dialogue.Responses)
            {
                AddSearchValue(values, response.Text);
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => string.Join(' ', entry.Value));
    }

    private static void AddSearchValue(HashSet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static void ClearRecordLists(RecordCollection records)
    {
        records.Npcs.Clear();
        records.Creatures.Clear();
        records.Races.Clear();
        records.Factions.Clear();
        records.Quests.Clear();
        records.DialogTopics.Clear();
        records.Dialogues.Clear();
        records.Notes.Clear();
        records.Books.Clear();
        records.Terminals.Clear();
        records.Scripts.Clear();
        records.Weapons.Clear();
        records.Armor.Clear();
        records.Ammo.Clear();
        records.Consumables.Clear();
        records.MiscItems.Clear();
        records.Keys.Clear();
        records.Containers.Clear();
        records.Perks.Clear();
        records.Spells.Clear();
        records.Cells.Clear();
        records.Worldspaces.Clear();
        records.MapMarkers.Clear();
        records.LeveledLists.Clear();
        records.GameSettings.Clear();
        records.Globals.Clear();
        records.Enchantments.Clear();
        records.BaseEffects.Clear();
        records.WeaponMods.Clear();
        records.Recipes.Clear();
        records.Challenges.Clear();
        records.Reputations.Clear();
        records.Projectiles.Clear();
        records.Explosions.Clear();
        records.Messages.Clear();
        records.Classes.Clear();
        records.Eyes.Clear();
        records.Hair.Clear();
        records.FormLists.Clear();
        records.Activators.Clear();
        records.Lights.Clear();
        records.Doors.Clear();
        records.Statics.Clear();
        records.Furniture.Clear();
        records.Packages.Clear();
        records.GenericRecords.Clear();
        records.Sounds.Clear();
        records.MusicTypes.Clear();
        records.TextureSets.Clear();
        records.ArmorAddons.Clear();
        records.Water.Clear();
        records.BodyPartData.Clear();
        records.ActorValueInfos.Clear();
        records.CombatStyles.Clear();
        records.LightingTemplates.Clear();
        records.NavMeshes.Clear();
        records.Weather.Clear();
    }

    private static Dictionary<uint, PlacedReferenceLocation> BuildPlacedReferenceLocations(
        IEnumerable<CellRecord> cells,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        var map = new Dictionary<uint, PlacedReferenceLocation>();
        foreach (var cell in cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.FormId == 0)
                {
                    continue;
                }

                map[obj.FormId] = new PlacedReferenceLocation(obj, cellInfo.FormId);
            }
        }

        return map;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>
        BuildNpcPlacementIndexes(IEnumerable<(string FilePath, RecordCollection Records)> sources)
    {
        var sourceList = sources.ToList();
        var virtualCellCanonicalFormIds = BuildVirtualCellCanonicalFormIds(sourceList.Select(source => source.Records));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, records) in sourceList)
        {
            result[filePath] = BuildNpcPlacementIndex(records, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>>
        BuildKeyLockedDoorIndexes(
            IEnumerable<(string FilePath, RecordCollection Records)> sources,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        var sourceList = sources.ToList();
        virtualCellCanonicalFormIds ??= BuildVirtualCellCanonicalFormIds(sourceList.Select(source => source.Records));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, records) in sourceList)
        {
            result[filePath] = BuildKeyLockedDoorIndex(records, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>
        BuildContainerPlacementIndexes(
            IEnumerable<(string FilePath, RecordCollection Records)> sources,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        var sourceList = sources.ToList();
        virtualCellCanonicalFormIds ??= BuildVirtualCellCanonicalFormIds(sourceList.Select(source => source.Records));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, records) in sourceList)
        {
            result[filePath] = BuildContainerPlacementIndex(records, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>
        BuildNpcScriptReferenceIndexes(IEnumerable<(string FilePath, RecordCollection Records)> sources)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, records) in sources)
        {
            result[filePath] = BuildNpcScriptReferenceIndex(records);
        }

        return result;
    }

    private static Dictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>> BuildNpcScriptReferenceIndex(
        RecordCollection records)
    {
        var npcFormIds = records.Npcs.Select(npc => npc.FormId).ToHashSet();
        if (npcFormIds.Count == 0 || records.Scripts.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>();
        }

        var map = new Dictionary<uint, List<NpcScriptReferenceInfo>>();
        foreach (var script in records.Scripts)
        {
            if (script.ReferencedObjects.Count == 0)
            {
                continue;
            }

            var reference = new NpcScriptReferenceInfo(
                script.FormId,
                script.EditorId,
                script.ScriptType,
                script.OwnerQuestFormId);
            foreach (var referencedFormId in script.ReferencedObjects.Distinct())
            {
                if (!npcFormIds.Contains(referencedFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(referencedFormId, out var scripts))
                {
                    scripts = [];
                    map[referencedFormId] = scripts;
                }

                scripts.Add(reference);
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<NpcScriptReferenceInfo>)entry.Value
                .OrderBy(reference => reference.ScriptEditorId ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.ScriptFormId)
                .ToList());
    }

    private static Dictionary<uint, IReadOnlyList<NpcPlacementInfo>> BuildNpcPlacementIndex(
        RecordCollection records,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        var npcFormIds = records.Npcs.Select(npc => npc.FormId).ToHashSet();
        if (npcFormIds.Count == 0 || records.Cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<NpcPlacementInfo>>();
        }

        var map = new Dictionary<uint, List<NpcPlacementInfo>>();
        foreach (var cell in records.Cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (!string.Equals(obj.RecordType, "ACHR", StringComparison.OrdinalIgnoreCase) ||
                    obj.BaseFormId == 0 ||
                    !npcFormIds.Contains(obj.BaseFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.BaseFormId, out var placements))
                {
                    placements = [];
                    map[obj.BaseFormId] = placements;
                }

                placements.Add(new NpcPlacementInfo(
                    obj,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<NpcPlacementInfo>)entry.Value
                .OrderBy(placement => ResolveCellSortName(placement), StringComparer.OrdinalIgnoreCase)
                .ThenBy(placement => placement.GridY ?? int.MaxValue)
                .ThenBy(placement => placement.GridX ?? int.MaxValue)
                .ThenBy(placement => placement.Ref.FormId)
                .ToList());
    }

    private static Dictionary<uint, IReadOnlyList<KeyLockedDoorInfo>> BuildKeyLockedDoorIndex(
        RecordCollection records,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (records.Keys.Count == 0 || records.Cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>();
        }

        var keyFormIds = records.Keys.Select(key => key.FormId).ToHashSet();
        var map = new Dictionary<uint, List<KeyLockedDoorInfo>>();
        foreach (var cell in records.Cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.LockKeyFormId is not > 0 ||
                    !keyFormIds.Contains(obj.LockKeyFormId.Value))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.LockKeyFormId.Value, out var doors))
                {
                    doors = [];
                    map[obj.LockKeyFormId.Value] = doors;
                }

                doors.Add(new KeyLockedDoorInfo(
                    obj,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<KeyLockedDoorInfo>)entry.Value
                .OrderBy(info => ResolveCellSortName(info), StringComparer.OrdinalIgnoreCase)
                .ThenBy(info => info.GridY ?? int.MaxValue)
                .ThenBy(info => info.GridX ?? int.MaxValue)
                .ThenBy(info => info.Ref.FormId)
                .ToList());
    }

    private static Dictionary<uint, IReadOnlyList<ContainerPlacementInfo>> BuildContainerPlacementIndex(
        RecordCollection records,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (records.Containers.Count == 0 || records.Cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<ContainerPlacementInfo>>();
        }

        var containerFormIds = records.Containers.Select(container => container.FormId).ToHashSet();
        var map = new Dictionary<uint, List<ContainerPlacementInfo>>();
        foreach (var cell in records.Cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (!string.Equals(obj.RecordType, "REFR", StringComparison.OrdinalIgnoreCase) ||
                    obj.BaseFormId == 0 ||
                    !containerFormIds.Contains(obj.BaseFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.BaseFormId, out var placements))
                {
                    placements = [];
                    map[obj.BaseFormId] = placements;
                }

                placements.Add(new ContainerPlacementInfo(
                    obj,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<ContainerPlacementInfo>)entry.Value
                .OrderBy(placement => ResolveCellSortName(placement), StringComparer.OrdinalIgnoreCase)
                .ThenBy(placement => placement.GridY ?? int.MaxValue)
                .ThenBy(placement => placement.GridX ?? int.MaxValue)
                .ThenBy(placement => placement.Ref.FormId)
                .ToList());
    }

    private static string ResolveCellSortName(NpcPlacementInfo placement)
    {
        if (!string.IsNullOrWhiteSpace(placement.CellEditorId))
        {
            return placement.CellEditorId;
        }

        if (!string.IsNullOrWhiteSpace(placement.CellName))
        {
            return placement.CellName;
        }

        return "";
    }

    private static string ResolveCellSortName(KeyLockedDoorInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.CellEditorId))
        {
            return info.CellEditorId;
        }

        if (!string.IsNullOrWhiteSpace(info.CellName))
        {
            return info.CellName;
        }

        return "";
    }

    private static string ResolveCellSortName(ContainerPlacementInfo placement)
    {
        if (!string.IsNullOrWhiteSpace(placement.CellEditorId))
        {
            return placement.CellEditorId;
        }

        if (!string.IsNullOrWhiteSpace(placement.CellName))
        {
            return placement.CellName;
        }

        return "";
    }

    private static PlacementCellInfo ResolvePlacementCellInfo(
        CellRecord cell,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (TryGetVirtualCellCanonicalFormId(cell, virtualCellCanonicalFormIds, out var canonicalCell))
        {
            return new PlacementCellInfo(
                canonicalCell.FormId,
                canonicalCell.IsSyntheticVirtual ? null : canonicalCell.EditorId ?? cell.EditorId,
                canonicalCell.IsSyntheticVirtual ? null : canonicalCell.DisplayName ?? cell.FullName,
                cell.WorldspaceFormId,
                cell.GridX,
                cell.GridY);
        }

        return new PlacementCellInfo(
            cell.FormId,
            cell.EditorId,
            cell.FullName,
            cell.WorldspaceFormId,
            cell.GridX,
            cell.GridY);
    }

    private static Dictionary<CellCoordinateKey, RealCellCandidate> BuildVirtualCellCanonicalFormIds(
        IEnumerable<RecordCollection> recordCollections)
    {
        var candidates = new Dictionary<CellCoordinateKey, Dictionary<uint, RealCellCandidate>>();
        var virtualOnlyKeys = new HashSet<CellCoordinateKey>();
        var allCellFormIds = new HashSet<uint>();
        foreach (var collection in recordCollections)
        {
            foreach (var cell in collection.Cells)
            {
                allCellFormIds.Add(cell.FormId);
                if (cell.IsVirtual &&
                    !cell.IsInterior &&
                    !cell.IsPersistentCell &&
                    !cell.IsUnresolvedBucket &&
                    TryGetCellCoordinateKey(cell, out var virtualKey))
                {
                    virtualOnlyKeys.Add(virtualKey);
                }

                if (!IsStableRealExteriorCell(cell) ||
                    !TryGetCellCoordinateKey(cell, out var key))
                {
                    continue;
                }

                if (!candidates.TryGetValue(key, out var formIds))
                {
                    formIds = [];
                    candidates[key] = formIds;
                }

                if (!formIds.TryGetValue(cell.FormId, out var existingCandidate))
                {
                    formIds[cell.FormId] = new RealCellCandidate(cell.FormId, cell.EditorId, cell.FullName, false);
                }
                else
                {
                    formIds[cell.FormId] = existingCandidate with
                    {
                        EditorId = existingCandidate.EditorId ?? cell.EditorId,
                        DisplayName = existingCandidate.DisplayName ?? cell.FullName
                    };
                }
            }
        }

        var canonical = new Dictionary<CellCoordinateKey, RealCellCandidate>();
        foreach (var (key, formIds) in candidates)
        {
            if (formIds.Count == 1)
            {
                canonical[key] = formIds.Values.Single();
            }
        }

        var nextSyntheticFormId = 0xFD000001u;
        foreach (var key in virtualOnlyKeys
                     .OrderBy(key => key.WorldspaceFormId)
                     .ThenBy(key => key.GridY)
                     .ThenBy(key => key.GridX))
        {
            if (canonical.ContainsKey(key))
            {
                continue;
            }

            while (allCellFormIds.Contains(nextSyntheticFormId))
            {
                nextSyntheticFormId++;
            }

            canonical[key] = new RealCellCandidate(nextSyntheticFormId, null, null, true);
            allCellFormIds.Add(nextSyntheticFormId);
            nextSyntheticFormId++;
        }

        return canonical;
    }

    private static bool TryGetVirtualCellCanonicalFormId(
        CellRecord cell,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> canonicalFormIds,
        out RealCellCandidate canonicalCell)
    {
        canonicalCell = default;
        if (!cell.IsVirtual ||
            cell.IsInterior ||
            cell.IsPersistentCell ||
            cell.IsUnresolvedBucket ||
            !TryGetCellCoordinateKey(cell, out var key))
        {
            return false;
        }

        return canonicalFormIds.TryGetValue(key, out canonicalCell);
    }

    private static bool IsStableRealExteriorCell(CellRecord cell)
    {
        return !cell.IsInterior
               && !cell.IsVirtual
               && !cell.IsPersistentCell
               && !cell.IsUnresolvedBucket
               && cell.FormId is > 0 and < 0xFE000000
               && cell.WorldspaceFormId.HasValue
               && cell.GridX.HasValue
               && cell.GridY.HasValue;
    }

    private static bool TryGetCellCoordinateKey(CellRecord cell, out CellCoordinateKey key)
    {
        if (cell.WorldspaceFormId.HasValue &&
            cell.GridX.HasValue &&
            cell.GridY.HasValue)
        {
            key = new CellCoordinateKey(cell.WorldspaceFormId.Value, cell.GridX.Value, cell.GridY.Value);
            return true;
        }

        key = default;
        return false;
    }

    private static RecordReport RebaseVirtualCellReport(RecordReport report, RealCellCandidate canonicalCell)
    {
        var sections = new List<ReportSection>(report.Sections.Count);
        foreach (var section in report.Sections)
        {
            var fields = new List<ReportField>(section.Fields.Count);
            foreach (var field in section.Fields)
            {
                if (field.Key == "FormID")
                {
                    fields.Add(field with { Value = ReportValue.String($"0x{canonicalCell.FormId:X8}") });
                }
                else if (field.Key == "Editor ID")
                {
                    if (canonicalCell.EditorId != null)
                    {
                        fields.Add(field with { Value = ReportValue.String(canonicalCell.EditorId) });
                    }
                }
                else if (field.Key == "Display Name")
                {
                    if (canonicalCell.DisplayName != null)
                    {
                        fields.Add(field with { Value = ReportValue.String(canonicalCell.DisplayName) });
                    }
                }
                else
                {
                    fields.Add(field);
                }
            }

            if (section.Name.Equals("Identity", StringComparison.OrdinalIgnoreCase))
            {
                if (canonicalCell.EditorId != null && fields.All(field => field.Key != "Editor ID"))
                {
                    fields.Add(new ReportField("Editor ID", ReportValue.String(canonicalCell.EditorId)));
                }

                if (canonicalCell.DisplayName != null && fields.All(field => field.Key != "Display Name"))
                {
                    fields.Add(new ReportField("Display Name", ReportValue.String(canonicalCell.DisplayName)));
                }
            }

            if (fields.Count > 0)
            {
                sections.Add(section with { Fields = fields });
            }
        }

        return report with
        {
            FormId = canonicalCell.FormId,
            EditorId = canonicalCell.EditorId,
            DisplayName = canonicalCell.DisplayName,
            Sections = sections
        };
    }

    private static void AddUpgradedVirtualCellForDump(
        Dictionary<uint, SortedDictionary<int, SortedSet<uint>>> upgradedVirtualCellIdsByDump,
        uint realFormId,
        int dumpIdx,
        uint originalVirtualFormId)
    {
        if (!upgradedVirtualCellIdsByDump.TryGetValue(realFormId, out var dumpMap))
        {
            dumpMap = [];
            upgradedVirtualCellIdsByDump[realFormId] = dumpMap;
        }

        if (!dumpMap.TryGetValue(dumpIdx, out var originalIds))
        {
            originalIds = [];
            dumpMap[dumpIdx] = originalIds;
        }

        originalIds.Add(originalVirtualFormId);
    }

    private static void AppendVirtualCellAuditMetadata(
        CrossDumpRecordIndex index,
        Dictionary<uint, SortedSet<uint>> upgradedVirtualCellIds,
        Dictionary<uint, SortedDictionary<int, SortedSet<uint>>> upgradedVirtualCellIdsByDump)
    {
        if (upgradedVirtualCellIds.Count == 0)
        {
            return;
        }

        if (!index.RecordMetadata.TryGetValue("Cell", out var cellMetadata))
        {
            cellMetadata = new Dictionary<uint, Dictionary<string, string>>();
            index.RecordMetadata["Cell"] = cellMetadata;
        }

        foreach (var (realFormId, originalVirtualFormIds) in upgradedVirtualCellIds)
        {
            if (!cellMetadata.TryGetValue(realFormId, out var metadata))
            {
                metadata = new Dictionary<string, string>();
                cellMetadata[realFormId] = metadata;
            }

            metadata["upgradedVirtualFormIds"] =
                string.Join(", ", originalVirtualFormIds.Select(formId => $"0x{formId:X8}"));
            if (upgradedVirtualCellIdsByDump.TryGetValue(realFormId, out var dumpMap))
            {
                metadata["upgradedVirtualFormIdsByDump"] = string.Join(
                    ";",
                    dumpMap.Select(dumpEntry =>
                        $"{dumpEntry.Key}:{string.Join(", ", dumpEntry.Value.Select(formId => $"0x{formId:X8}"))}"));
            }
        }
    }

    internal readonly record struct CellCoordinateKey(uint WorldspaceFormId, int GridX, int GridY);

    internal readonly record struct RealCellCandidate(
        uint FormId,
        string? EditorId,
        string? DisplayName,
        bool IsSyntheticVirtual);

    private readonly record struct PlacementCellInfo(
        uint FormId,
        string? EditorId,
        string? DisplayName,
        uint? WorldspaceFormId,
        int? GridX,
        int? GridY);

    /// <summary>
    ///     Returns true if a resolver-returned name is a real name (not null, empty,
    ///     or a placeholder like "(none)").
    /// </summary>
    private static bool IsRealName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name == "(none)") return false;
        return true;
    }

    /// <summary>
    ///     Resolve a quest group label using canonical quest labels.
    ///     Tracks name history when a quest's display name changes across builds.
    /// </summary>
    private static string ResolveQuestGroupLabel(
        uint questFormId, FormIdResolver resolver, Dictionary<uint, string> questLabels)
    {
        var questEditorId = resolver.GetEditorId(questFormId);
        var questDisplayName = resolver.GetDisplayName(questFormId);

        if (!questLabels.TryGetValue(questFormId, out var existingLabel))
        {
            var label = BuildQuestLabel(questFormId, questDisplayName, questEditorId);
            questLabels[questFormId] = label;
            return label;
        }

        // Update label with better resolution from later dumps
        if (!string.IsNullOrEmpty(questDisplayName) && !existingLabel.Contains(" \u2192 "))
        {
            var betterLabel = $"{questDisplayName} ({questEditorId ?? Fmt.FIdAlways(questFormId)})";
            if (betterLabel != existingLabel)
            {
                // Only record name history if the OLD label also had a display name
                // (contained parens = "Name (EditorID)" format). If it was EditorID-only,
                // this is just better resolution, not a real name change.
                var hadDisplayName = existingLabel.Contains('(');
                if (hadDisplayName)
                {
                    var parenIdx = existingLabel.LastIndexOf('(');
                    var oldName = existingLabel[..parenIdx].TrimEnd();
                    if (oldName != questDisplayName)
                    {
                        questLabels[questFormId] =
                            $"{oldName} \u2192 {questDisplayName} ({questEditorId ?? Fmt.FIdAlways(questFormId)})";
                    }
                    else
                    {
                        questLabels[questFormId] = betterLabel;
                    }
                }
                else
                {
                    // Silent upgrade from EditorID-only to Name (EditorID)
                    questLabels[questFormId] = betterLabel;
                }
            }
        }

        return questLabels[questFormId];
    }

    /// <summary>
    ///     Resolve the NPC group label for a dialogue record, using canonical speaker labels
    ///     or falling back to quest for speaker-less generic dialogue.
    ///     Updates the speaker label if a better name is resolved in a later dump.
    /// </summary>
    private static string ResolveSpeakerGroupLabel(
        DialogueRecord d, FormIdResolver resolver, Dictionary<uint, string> speakerLabels)
    {
        if (d.SpeakerFormId.HasValue)
        {
            // Always try to improve the speaker label with better name resolution
            var speakerName = resolver.GetBestName(d.SpeakerFormId.Value);
            var speakerEditorId = resolver.GetEditorId(d.SpeakerFormId.Value);

            if (!speakerLabels.TryGetValue(d.SpeakerFormId.Value, out var existingLabel))
            {
                // First time — create label
                var label = BuildSpeakerLabel(d.SpeakerFormId.Value, speakerName, speakerEditorId);
                speakerLabels[d.SpeakerFormId.Value] = label;
                return label;
            }

            // Update label with better resolution from later dumps
            if (!string.IsNullOrEmpty(speakerName))
            {
                var betterLabel = $"{speakerName} (0x{d.SpeakerFormId.Value:X8})";
                if (betterLabel != existingLabel && !existingLabel.Contains(" \u2192 "))
                {
                    var oldName = existingLabel.Replace($" (0x{d.SpeakerFormId.Value:X8})", "");
                    // Only record history if the old name was a real display name,
                    // not an EditorID fallback or a FormID-only placeholder like "NPC 0x..."
                    var oldWasFallback = string.Equals(oldName, speakerEditorId,
                                             StringComparison.OrdinalIgnoreCase)
                                         || oldName.StartsWith("NPC 0x", StringComparison.Ordinal);
                    if (!oldWasFallback && oldName != speakerName)
                    {
                        speakerLabels[d.SpeakerFormId.Value] =
                            $"{speakerName} \u2192 {oldName} (0x{d.SpeakerFormId.Value:X8})";
                    }
                    else
                    {
                        // Silent upgrade: EditorID → display name, or same name
                        speakerLabels[d.SpeakerFormId.Value] = betterLabel;
                    }
                }
            }

            return speakerLabels[d.SpeakerFormId.Value];
        }

        if (d.QuestFormId.HasValue)
        {
            var questName = resolver.GetBestName(d.QuestFormId.Value);
            var questEditorId = resolver.GetEditorId(d.QuestFormId.Value);
            var questLabel = PickRealName(questName, questEditorId);
            return questLabel != null ? $"{questLabel} (quest)" : $"Quest 0x{d.QuestFormId.Value:X8}";
        }

        return "(No Speaker)";
    }

    private static string? PickRealName(string? displayName, string? editorId)
    {
        if (IsRealName(displayName))
        {
            return displayName;
        }

        return IsRealName(editorId) ? editorId : null;
    }

    private static string PickFirstDialogueText(DialogueRecord dialogue)
    {
        if (!string.IsNullOrWhiteSpace(dialogue.PromptText))
        {
            return dialogue.PromptText;
        }

        return dialogue.Responses
            .Select(response => response.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";
    }

    private static string BuildQuestLabel(uint questFormId, string? displayName, string? editorId)
    {
        if (!string.IsNullOrEmpty(displayName))
        {
            return $"{displayName} ({editorId ?? Fmt.FIdAlways(questFormId)})";
        }

        return !string.IsNullOrEmpty(editorId) ? editorId : $"Quest 0x{questFormId:X8}";
    }

    private static string BuildSpeakerLabel(uint speakerFormId, string? displayName, string? editorId)
    {
        if (!string.IsNullOrEmpty(displayName))
        {
            return $"{displayName} (0x{speakerFormId:X8})";
        }

        return !string.IsNullOrEmpty(editorId)
            ? $"{editorId} (0x{speakerFormId:X8})"
            : $"NPC 0x{speakerFormId:X8}";
    }
}

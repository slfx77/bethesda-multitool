using System.Globalization;
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
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>? npcPlacementIndexes =
            null,
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
        // Tracks per-FormID display-name history + EditorID so the finalization pass
        // can render rename chains (Old → New) and disambiguate distinct worldspaces
        // that happen to share a display name (e.g., a split FreesideWorld).
        var worldspaceLabels = new Dictionary<uint, WorldspaceLabelHistory>();
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

            // Build a per-dump WRLD lookup straight from records.Worldspaces. The
            // Resolver's DisplayNames dict only sees worldspace names captured via
            // FULL-subrecord scans; runtime-extracted worldspaces (DMPs) populate
            // WorldspaceRecord.FullName via the PDB struct read but never make it
            // back into FormIdToFullName. Without this map, RecordWorldspaceObservation
            // gets "(none)" for every DMP-era worldspace name and the group label
            // collapses to the latest (post-rename) name only.
            var worldspaceNamesByFormId = ShouldIncludeType(allowedTypes, "Cell")
                ? BuildWorldspaceNameLookup(dump.Records.Worldspaces)
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

                        // Record display name + EditorID for this worldspace FormID
                        // on each dump. The finalization pass builds the human label
                        // from the accumulated history. Prefer the per-dump
                        // WorldspaceRecord (covers runtime-extracted DMP worldspaces);
                        // fall back to the resolver only if the worldspace isn't in
                        // this dump's records (rare — e.g., cell references a missing
                        // worldspace).
                        string? wsDisplayName = null;
                        string? wsEditorId = null;
                        if (worldspaceNamesByFormId != null &&
                            worldspaceNamesByFormId.TryGetValue(wsFid, out var wsNames))
                        {
                            wsDisplayName = wsNames.FullName;
                            wsEditorId = wsNames.EditorId;
                        }

                        wsDisplayName ??= dump.Resolver.ResolveDisplayName(wsFid);
                        wsEditorId ??= dump.Resolver.ResolveEditorId(wsFid);
                        RecordWorldspaceObservation(wsFid, wsDisplayName, wsEditorId, worldspaceLabels);
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
        // resolved worldspace label. Labels include rename history ("Old → New")
        // and a stable identifier (EditorID, with FormID disambiguator on collisions)
        // so two FormIDs that share a display name don't merge into one group.
        // Falls back to "Worldspace 0xFORMID" if no name was resolved in any dump.
        if (index.RecordGroups.TryGetValue("Cell", out var cellGroups))
        {
            var resolvedWorldspaceLabels = ResolveWorldspaceLabels(worldspaceLabels);
            foreach (var (cellFormId, currentKey) in cellGroups.ToList())
            {
                if (currentKey.StartsWith("WS:0x", StringComparison.Ordinal))
                {
                    var hex = currentKey[5..];
                    if (uint.TryParse(hex, NumberStyles.HexNumber, null, out var wsFid))
                    {
                        cellGroups[cellFormId] = resolvedWorldspaceLabels.TryGetValue(wsFid, out var label)
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
        records.RecipeCategories.Clear();
        records.ConstructibleObjects.Clear();
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
        return CrossDumpPlacementIndexBuilder.BuildPlacedReferenceLocations(cells, virtualCellCanonicalFormIds);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>
        BuildNpcPlacementIndexes(IEnumerable<(string FilePath, RecordCollection Records)> sources)
    {
        return CrossDumpPlacementIndexBuilder.BuildNpcPlacementIndexes(sources);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>>
        BuildKeyLockedDoorIndexes(
            IEnumerable<(string FilePath, RecordCollection Records)> sources,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        return CrossDumpPlacementIndexBuilder.BuildKeyLockedDoorIndexes(sources, virtualCellCanonicalFormIds);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>
        BuildContainerPlacementIndexes(
            IEnumerable<(string FilePath, RecordCollection Records)> sources,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        return CrossDumpPlacementIndexBuilder.BuildContainerPlacementIndexes(sources, virtualCellCanonicalFormIds);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>
        BuildNpcScriptReferenceIndexes(IEnumerable<(string FilePath, RecordCollection Records)> sources)
    {
        return CrossDumpPlacementIndexBuilder.BuildNpcScriptReferenceIndexes(sources);
    }

    private static Dictionary<CellCoordinateKey, RealCellCandidate> BuildVirtualCellCanonicalFormIds(
        IEnumerable<RecordCollection> recordCollections)
    {
        return VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(recordCollections);
    }

    private static bool TryGetVirtualCellCanonicalFormId(
        CellRecord cell,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> canonicalFormIds,
        out RealCellCandidate canonicalCell)
    {
        return VirtualCellCanonicalizer.TryGetVirtualCellCanonicalFormId(cell, canonicalFormIds, out canonicalCell);
    }

    private static RecordReport RebaseVirtualCellReport(RecordReport report, RealCellCandidate canonicalCell)
    {
        return VirtualCellCanonicalizer.RebaseVirtualCellReport(report, canonicalCell);
    }

    private static void AddUpgradedVirtualCellForDump(
        Dictionary<uint, SortedDictionary<int, SortedSet<uint>>> upgradedVirtualCellIdsByDump,
        uint realFormId,
        int dumpIdx,
        uint originalVirtualFormId)
    {
        VirtualCellCanonicalizer.AddUpgradedVirtualCellForDump(
            upgradedVirtualCellIdsByDump,
            realFormId,
            dumpIdx,
            originalVirtualFormId);
    }

    private static void AppendVirtualCellAuditMetadata(
        CrossDumpRecordIndex index,
        Dictionary<uint, SortedSet<uint>> upgradedVirtualCellIds,
        Dictionary<uint, SortedDictionary<int, SortedSet<uint>>> upgradedVirtualCellIdsByDump)
    {
        VirtualCellCanonicalizer.AppendVirtualCellAuditMetadata(
            index,
            upgradedVirtualCellIds,
            upgradedVirtualCellIdsByDump);
    }

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
    internal static string ResolveQuestGroupLabel(
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

    /// <summary>
    ///     Overload consuming nullable speaker/quest FormIDs directly — used by the
    ///     projection-based aggregator where the dialogue record has been released and only
    ///     the captured observation remains. Constructs a synthetic <see cref="DialogueRecord" />
    ///     and delegates to the existing implementation so the label-history logic stays in
    ///     exactly one place.
    /// </summary>
    internal static string ResolveSpeakerGroupLabel(
        uint? speakerFormId,
        uint? questFormId,
        FormIdResolver resolver,
        Dictionary<uint, string> speakerLabels)
    {
        var synthetic = new DialogueRecord
        {
            FormId = 0,
            SpeakerFormId = speakerFormId,
            QuestFormId = questFormId
        };
        return ResolveSpeakerGroupLabel(synthetic, resolver, speakerLabels);
    }

    private static string? PickRealName(string? displayName, string? editorId)
    {
        if (IsRealName(displayName))
        {
            return displayName;
        }

        return IsRealName(editorId) ? editorId : null;
    }

    internal sealed class WorldspaceLabelHistory
    {
        public List<string> DisplayNames { get; } = [];
        public string? EditorId { get; set; }
    }

    private static Dictionary<uint, (string? EditorId, string? FullName)> BuildWorldspaceNameLookup(
        IReadOnlyList<WorldspaceRecord> worldspaces)
    {
        var lookup = new Dictionary<uint, (string? EditorId, string? FullName)>(worldspaces.Count);
        foreach (var worldspace in worldspaces)
        {
            if (worldspace.FormId == 0)
            {
                continue;
            }

            lookup[worldspace.FormId] = (worldspace.EditorId, worldspace.FullName);
        }

        return lookup;
    }

    internal static void RecordWorldspaceObservation(
        uint wsFid,
        string? displayName,
        string? editorId,
        Dictionary<uint, WorldspaceLabelHistory> worldspaceLabels)
    {
        if (!worldspaceLabels.TryGetValue(wsFid, out var history))
        {
            history = new WorldspaceLabelHistory();
            worldspaceLabels[wsFid] = history;
        }

        if (IsRealName(editorId))
        {
            history.EditorId = editorId;
        }

        if (!IsRealName(displayName))
        {
            return;
        }

        // Append only if the name differs from the most recent observation so a
        // name appearing across multiple dumps doesn't produce "A → A" noise,
        // but a real revert (A → B → A) is preserved.
        if (history.DisplayNames.Count == 0 ||
            !string.Equals(history.DisplayNames[^1], displayName, StringComparison.Ordinal))
        {
            history.DisplayNames.Add(displayName!);
        }
    }

    internal static Dictionary<uint, string> ResolveWorldspaceLabels(
        Dictionary<uint, WorldspaceLabelHistory> worldspaceLabels)
    {
        var labels = new Dictionary<uint, string>(worldspaceLabels.Count);
        foreach (var (wsFid, history) in worldspaceLabels)
        {
            labels[wsFid] = BuildWorldspaceLabel(wsFid, history);
        }

        DisambiguateWorldspaceLabelCollisions(labels);
        return labels;
    }

    private static string BuildWorldspaceLabel(uint wsFid, WorldspaceLabelHistory history)
    {
        if (history.DisplayNames.Count == 0)
        {
            return history.EditorId ?? $"Worldspace 0x{wsFid:X8}";
        }

        var identifier = history.EditorId ?? $"0x{wsFid:X8}";
        var names = string.Join(" → ", history.DisplayNames);
        return $"{names} ({identifier})";
    }

    private static void DisambiguateWorldspaceLabelCollisions(Dictionary<uint, string> labels)
    {
        var labelToFormIds = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        foreach (var (wsFid, label) in labels)
        {
            if (!labelToFormIds.TryGetValue(label, out var ids))
            {
                ids = [];
                labelToFormIds[label] = ids;
            }

            ids.Add(wsFid);
        }

        foreach (var (_, ids) in labelToFormIds)
        {
            if (ids.Count <= 1)
            {
                continue;
            }

            foreach (var wsFid in ids)
            {
                labels[wsFid] = AppendFormIdDisambiguator(labels[wsFid], wsFid);
            }
        }
    }

    private static string AppendFormIdDisambiguator(string label, uint wsFid)
    {
        // Inject "@ 0xFORMID" inside the existing trailing parenthetical so the
        // result reads "Name (EditorID @ 0xFORMID)". If the label has no trailing
        // parens, append a fresh one: "EditorID (0xFORMID)".
        if (label.EndsWith(')'))
        {
            return $"{label[..^1]} @ 0x{wsFid:X8})";
        }

        return $"{label} (0x{wsFid:X8})";
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

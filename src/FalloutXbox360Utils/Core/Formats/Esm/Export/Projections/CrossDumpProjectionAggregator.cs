using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Projection-based replacement for <see cref="CrossDumpAggregator.Aggregate" />. Consumes
///     pre-projected <see cref="CrossDumpSourceProjection" />s instead of raw
///     <c>RecordCollection</c>s, so the heavy source data can be released before aggregation
///     runs. Output shape (<see cref="CrossDumpRecordIndex" />) is identical to
///     <c>Aggregate</c>'s, so downstream HTML / JSON writers don't change.
/// </summary>
/// <remarks>
///     <para>
///         <b>Pipeline-level pass-B.</b> Caller is responsible for building cross-source
///         indexes (npc placements, key locked doors, container placements, npc script
///         references) once across all projections, then calling
///         <see cref="BuildLatePassReports" /> to populate the NPC / Key / Container slots
///         of <see cref="CrossDumpSourceProjection.ReportsByType" />. After that, this
///         aggregator can iterate the projection's ReportsByType directly for every type.
///     </para>
///     <para>
///         <b>Chronological replay.</b> Projections are sorted by
///         <see cref="CrossDumpSourceProjection.BuildDateUtc" /> exactly once. The
///         per-projection inner loop replays
///         <see cref="CrossDumpSourceProjection.WorldspaceObservations" /> /
///         <see cref="CrossDumpSourceProjection.CellGroupObservations" /> /
///         <see cref="CrossDumpSourceProjection.DialogueObservations" /> in chronological
///         order, mirroring the original aggregator's per-dump loop. This is the invariant
///         that preserves worldspace rename history
///         ("Camp McCarran Tarmac → Camp McCarran (CampMcCarranWorld)") and the
///         ESM-authority gate for cell group assignment.
///     </para>
/// </remarks>
internal static class CrossDumpProjectionAggregator
{
    /// <summary>
    ///     Replace every projection's <see cref="CrossDumpSourceProjection.LateEnrichment" />
    ///     with <c>null</c>, releasing the held NPC / Key / Container record lists once
    ///     pass-B has already built their reports. Mutates the list in place.
    /// </summary>
    /// <remarks>
    ///     Memory: across 50 DMPs each holding a few thousand NPC + a few hundred Key /
    ///     Container records, this typically frees tens to hundreds of MB. Smaller than the
    ///     CellRecord / DialogueRecord drop that projection itself enables, but still worth
    ///     reclaiming before the per-record-type aggregation loop, where peak HTML JSON
    ///     payloads can be hundreds of MB on their own.
    /// </remarks>
    internal static void ReleaseLateEnrichment(List<CrossDumpSourceProjection> projections)
    {
        for (var i = 0; i < projections.Count; i++)
        {
            if (projections[i].LateEnrichment is not null)
            {
                projections[i] = projections[i] with { LateEnrichment = null };
            }
        }
    }

    /// <summary>
    ///     Build pass-B reports for NPC / Key / Container records across every projection.
    ///     Mutates each projection's <see cref="CrossDumpSourceProjection.ReportsByType" /> to
    ///     add the late-enrichment-dependent entries. After this call, every projection has a
    ///     complete report set for all record types.
    /// </summary>
    /// <remarks>
    ///     Cross-source indexes are looked up per source via the <c>FilePath</c> key. Pass
    ///     empty dictionaries (or <c>null</c>) for any record type the caller doesn't need.
    /// </remarks>
    internal static void BuildLatePassReports(
        IReadOnlyList<CrossDumpSourceProjection> projections,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>? npcPlacementIndexes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>?
            npcScriptReferenceIndexes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>>? keyLockedDoorIndexes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>?
            containerPlacementIndexes)
    {
        foreach (var projection in projections)
        {
            if (projection.LateEnrichment is not { } late)
            {
                continue;
            }

            var resolver = projection.Resolver;
            var path = projection.FilePath;

            var npcPlacements = LookupBy(npcPlacementIndexes, path);
            var npcScriptRefs = LookupBy(npcScriptReferenceIndexes, path);
            var keyDoors = LookupBy(keyLockedDoorIndexes, path);
            var containerPlacements = LookupBy(containerPlacementIndexes, path);

            BuildLatePassNpcReports(projection, late.Npcs, resolver, npcPlacements, npcScriptRefs);
            BuildLatePassKeyReports(projection, late.Keys, resolver, keyDoors);
            BuildLatePassContainerReports(projection, late.Containers, resolver, containerPlacements);
        }
    }

    /// <summary>
    ///     Aggregate one record type across all projections. Sort happens here (every call
    ///     ends up with the same chronological order — fine because the sort is cheap), but
    ///     cross-source indexes must already be built and pass-B reports already populated
    ///     by <see cref="BuildLatePassReports" />.
    /// </summary>
    internal static CrossDumpRecordIndex AggregateFromProjections(
        IEnumerable<CrossDumpSourceProjection> projections,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds,
        IReadOnlySet<string>? allowedTypes = null)
    {
        var index = new CrossDumpRecordIndex();
        var ordered = projections.OrderBy(p => p.BuildDateUtc).ToList();

        var upgradedVirtualCellIds = new Dictionary<uint, SortedSet<uint>>();
        var upgradedVirtualCellIdsByDump = new Dictionary<uint, SortedDictionary<int, SortedSet<uint>>>();

        // Per-FormID display-name history for chronological replay (mirrors the existing
        // worldspaceLabels in CrossDumpAggregator.Aggregate so the rename-chain finalize
        // step works identically).
        var worldspaceLabels = new Dictionary<uint, CrossDumpAggregator.WorldspaceLabelHistory>();
        var speakerLabels = new Dictionary<uint, string>();
        var questLabels = new Dictionary<uint, string>();
        var cellGroupFromEsm = new HashSet<uint>();

        for (var dumpIdx = 0; dumpIdx < ordered.Count; dumpIdx++)
        {
            var projection = ordered[dumpIdx];
            index.Dumps.Add(new DumpSnapshot(
                Path.GetFileName(projection.FilePath),
                projection.BuildDateUtc,
                projection.ShortName,
                projection.IsDmp,
                DateSource: projection.DateSource));

            foreach (var (typeName, reports) in projection.ReportsByType)
            {
                if (!ShouldIncludeType(allowedTypes, typeName))
                {
                    continue;
                }

                foreach (var (formId, originalReport) in reports)
                {
                    var report = originalReport;
                    var reportFormId = formId;
                    var wasRebasedVirtualCell = false;
                    var canonicalCell = default(RealCellCandidate);

                    if (string.Equals(typeName, "Cell", StringComparison.OrdinalIgnoreCase))
                    {
                        var skeleton = FindCellSkeleton(projection, formId);
                        if (skeleton != null &&
                            VirtualCellCanonicalizer.TryGetVirtualCellCanonicalFormId(
                                skeleton, virtualCellCanonicalFormIds, out canonicalCell))
                        {
                            reportFormId = canonicalCell.FormId;
                            wasRebasedVirtualCell = true;
                            if (!upgradedVirtualCellIds.TryGetValue(reportFormId, out var originalIds))
                            {
                                originalIds = [];
                                upgradedVirtualCellIds[reportFormId] = originalIds;
                            }

                            originalIds.Add(formId);
                            VirtualCellCanonicalizer.AddUpgradedVirtualCellForDump(
                                upgradedVirtualCellIdsByDump, reportFormId, dumpIdx, formId);

                            report = VirtualCellCanonicalizer.RebaseVirtualCellReport(report, canonicalCell);
                        }
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

                    // Match the original Aggregate's IsVirtual+already-seen guard. When two
                    // virtual cells from the same dump collapse onto the same canonical
                    // FormID, the first observation wins. _ = wasRebasedVirtualCell silences
                    // an unused-variable warning if no virtual cell ever rebased.
                    _ = wasRebasedVirtualCell;
                    var skeletonForGuard = string.Equals(typeName, "Cell", StringComparison.OrdinalIgnoreCase)
                        ? FindCellSkeleton(projection, formId)
                        : null;
                    if (skeletonForGuard is { IsVirtual: true } && structDumpMap.ContainsKey(dumpIdx))
                    {
                        continue;
                    }

                    structDumpMap[dumpIdx] = report;
                }
            }

            // Replay cell-group observations (chronological order matters: ESM authority
            // gate + DMP→DMP upgrade are time-sensitive).
            if (ShouldIncludeType(allowedTypes, "Cell"))
            {
                ReplayCellGroupObservations(
                    index, projection, virtualCellCanonicalFormIds, worldspaceLabels, cellGroupFromEsm);
            }

            // Replay dialogue observations.
            if (ShouldIncludeType(allowedTypes, "Dialogue"))
            {
                ReplayDialogueObservations(index, projection, questLabels, speakerLabels);
            }

            // Apply dialog topic search text to RecordMetadata.
            if (ShouldIncludeType(allowedTypes, "DialogTopic"))
            {
                ApplyDialogTopicSearchTextMetadata(index, projection);
            }
        }

        VirtualCellCanonicalizer.AppendVirtualCellAuditMetadata(
            index, upgradedVirtualCellIds, upgradedVirtualCellIdsByDump);

        // Finalize: replace cell group placeholder keys ("WS:0xFORMID") with the resolved
        // worldspace label (rename history + EditorID disambiguation). Mirrors the original
        // Aggregate's tail.
        if (index.RecordGroups.TryGetValue("Cell", out var cellGroups))
        {
            var resolvedWorldspaceLabels = CrossDumpAggregator.ResolveWorldspaceLabels(worldspaceLabels);
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

        FinalizeDialogueGroupLabels(index, speakerLabels, questLabels);

        return index;
    }

    // ---------- Helpers ----------

    private static bool ShouldIncludeType(IReadOnlySet<string>? allowedTypes, string typeName)
    {
        return allowedTypes is not { Count: > 0 } || allowedTypes.Contains(typeName);
    }

    private static IReadOnlyDictionary<uint, IReadOnlyList<T>>? LookupBy<T>(
        IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<T>>>? indexByPath,
        string path)
    {
        if (indexByPath != null && indexByPath.TryGetValue(path, out var byFormId))
        {
            return byFormId;
        }

        return null;
    }

    private static CellSkeleton? FindCellSkeleton(CrossDumpSourceProjection projection, uint formId)
    {
        // Linear scan — projections are small enough per source that a dict would just
        // duplicate memory; if profiling shows this hot, swap in a precomputed lookup
        // on the projection itself.
        foreach (var skeleton in projection.CellSkeletons)
        {
            if (skeleton.FormId == formId)
            {
                return skeleton;
            }
        }

        return null;
    }

    private static void BuildLatePassNpcReports(
        CrossDumpSourceProjection projection,
        IReadOnlyList<NpcRecord> npcs,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>? placements,
        IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>? scriptReferences)
    {
        if (npcs.Count == 0)
        {
            return;
        }

        var reports = GetOrAddTypeBucket(projection.ReportsByType, "NPC");
        foreach (var npc in npcs)
        {
            var report = RecordTextFormatter.BuildReport(
                npc, resolver,
                npcPlacements: placements,
                npcScriptReferences: scriptReferences);
            if (report != null)
            {
                reports[npc.FormId] = report;
            }
        }
    }

    private static void BuildLatePassKeyReports(
        CrossDumpSourceProjection projection,
        IReadOnlyList<KeyRecord> keys,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>? doors)
    {
        if (keys.Count == 0)
        {
            return;
        }

        var reports = GetOrAddTypeBucket(projection.ReportsByType, "Key");
        foreach (var key in keys)
        {
            var report = RecordTextFormatter.BuildReport(key, resolver, keyLockedDoors: doors);
            if (report != null)
            {
                reports[key.FormId] = report;
            }
        }
    }

    private static void BuildLatePassContainerReports(
        CrossDumpSourceProjection projection,
        IReadOnlyList<ContainerRecord> containers,
        FormIdResolver resolver,
        IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>? placements)
    {
        if (containers.Count == 0)
        {
            return;
        }

        var reports = GetOrAddTypeBucket(projection.ReportsByType, "Container");
        foreach (var container in containers)
        {
            var report = RecordTextFormatter.BuildReport(container, resolver, containerPlacements: placements);
            if (report != null)
            {
                reports[container.FormId] = report;
            }
        }
    }

    private static Dictionary<uint, RecordReport> GetOrAddTypeBucket(
        Dictionary<string, Dictionary<uint, RecordReport>> reportsByType,
        string typeName)
    {
        if (!reportsByType.TryGetValue(typeName, out var bucket))
        {
            bucket = new Dictionary<uint, RecordReport>();
            reportsByType[typeName] = bucket;
        }

        return bucket;
    }

    private static void ReplayCellGroupObservations(
        CrossDumpRecordIndex index,
        CrossDumpSourceProjection projection,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds,
        Dictionary<uint, CrossDumpAggregator.WorldspaceLabelHistory> worldspaceLabels,
        HashSet<uint> cellGroupFromEsm)
    {
        if (!index.RecordGroups.TryGetValue("Cell", out var gm))
        {
            gm = new Dictionary<uint, string>();
            index.RecordGroups["Cell"] = gm;
        }

        foreach (var observation in projection.CellGroupObservations)
        {
            // Resolve the post-virtual-canonicalization cell FormID.
            var skeleton = FindCellSkeleton(projection, observation.CellFormId);
            var reportFormId = observation.CellFormId;
            if (skeleton != null &&
                VirtualCellCanonicalizer.TryGetVirtualCellCanonicalFormId(
                    skeleton, virtualCellCanonicalFormIds, out var canonical))
            {
                reportFormId = canonical.FormId;
            }

            string newGroupKey;
            if (observation.IsInterior)
            {
                newGroupKey = "Interior Cells";
            }
            else if (observation.WorldspaceFormId.HasValue)
            {
                var wsFid = observation.WorldspaceFormId.Value;
                newGroupKey = $"WS:0x{wsFid:X8}";

                // Worldspace label history: prefer the per-projection WorldspaceNames lookup
                // (covers runtime-extracted DMP worldspaces); fall back to the resolver if
                // the worldspace isn't in this projection's records.
                string? wsDisplayName = null;
                string? wsEditorId = null;
                if (projection.WorldspaceNames.TryGetValue(wsFid, out var wsNames))
                {
                    wsDisplayName = wsNames.FullName;
                    wsEditorId = wsNames.EditorId;
                }

                wsDisplayName ??= projection.Resolver.ResolveDisplayName(wsFid);
                wsEditorId ??= projection.Resolver.ResolveEditorId(wsFid);
                CrossDumpAggregator.RecordWorldspaceObservation(
                    wsFid, wsDisplayName, wsEditorId, worldspaceLabels);
            }
            else
            {
                newGroupKey = "Exterior Cells (Unknown Worldspace)";
            }

            // ESM authority gate (matches original Aggregate logic exactly).
            if (!gm.TryGetValue(reportFormId, out var existingGroup))
            {
                gm[reportFormId] = newGroupKey;
                if (!projection.IsDmp)
                {
                    cellGroupFromEsm.Add(reportFormId);
                }
            }
            else if (!projection.IsDmp)
            {
                gm[reportFormId] = newGroupKey;
                cellGroupFromEsm.Add(reportFormId);
            }
            else if (!cellGroupFromEsm.Contains(reportFormId))
            {
                // DMP→DMP upgrade: only allow Interior or Unknown→worldspace.
                if (observation.IsInterior && existingGroup != "Interior Cells")
                {
                    gm[reportFormId] = "Interior Cells";
                }
                else if (existingGroup == "Exterior Cells (Unknown Worldspace)"
                         && observation.WorldspaceFormId.HasValue)
                {
                    gm[reportFormId] = newGroupKey;
                }
            }

            // Grid coordinates (latest wins, ESM coords beat DMP-inferred coords).
            if (observation.GridX.HasValue && observation.GridY.HasValue)
            {
                index.CellGridCoords[reportFormId] = (observation.GridX.Value, observation.GridY.Value);
            }
        }
    }

    private static void ReplayDialogueObservations(
        CrossDumpRecordIndex index,
        CrossDumpSourceProjection projection,
        Dictionary<uint, string> questLabels,
        Dictionary<uint, string> speakerLabels)
    {
        if (projection.DialogueObservations.Count == 0)
        {
            return;
        }

        if (!index.RecordGroups.TryGetValue("Dialogue_Quest", out var questGroups))
        {
            questGroups = new Dictionary<uint, string>();
            index.RecordGroups["Dialogue_Quest"] = questGroups;
        }

        if (!index.RecordGroups.TryGetValue("Dialogue_NPC", out var npcGroups))
        {
            npcGroups = new Dictionary<uint, string>();
            index.RecordGroups["Dialogue_NPC"] = npcGroups;
        }

        if (!index.RecordMetadata.TryGetValue("Dialogue", out var metaMap))
        {
            metaMap = new Dictionary<uint, Dictionary<string, string>>();
            index.RecordMetadata["Dialogue"] = metaMap;
        }

        var resolver = projection.Resolver;

        foreach (var (formId, observation) in projection.DialogueObservations)
        {
            // Quest group.
            if (!questGroups.TryGetValue(formId, out var existingQuestGroup))
            {
                questGroups[formId] = observation.QuestFormId.HasValue
                    ? CrossDumpAggregator.ResolveQuestGroupLabel(observation.QuestFormId.Value, resolver, questLabels)
                    : "(No Quest)";
            }
            else if (existingQuestGroup == "(No Quest)" && observation.QuestFormId.HasValue)
            {
                questGroups[formId] =
                    CrossDumpAggregator.ResolveQuestGroupLabel(observation.QuestFormId.Value, resolver, questLabels);
            }
            else if (observation.QuestFormId.HasValue)
            {
                CrossDumpAggregator.ResolveQuestGroupLabel(observation.QuestFormId.Value, resolver, questLabels);
            }

            // NPC group (speaker label).
            var hasExistingNpcGroup = npcGroups.TryGetValue(formId, out var existingNpcGroup);
            var canUpgradeNoSpeaker = hasExistingNpcGroup
                                      && existingNpcGroup == "(No Speaker)"
                                      && (observation.SpeakerFormId.HasValue || observation.QuestFormId.HasValue);
            if (!hasExistingNpcGroup || canUpgradeNoSpeaker)
            {
                npcGroups[formId] = CrossDumpAggregator.ResolveSpeakerGroupLabel(
                    observation.SpeakerFormId,
                    observation.QuestFormId,
                    resolver,
                    speakerLabels);
            }

            // Per-record metadata.
            if (!metaMap.TryGetValue(formId, out var meta))
            {
                meta = new Dictionary<string, string>();
                metaMap[formId] = meta;
            }

            if (observation.QuestFormId.HasValue)
            {
                meta["questFormId"] = $"0x{observation.QuestFormId.Value:X8}";
                var qEid = resolver.GetEditorId(observation.QuestFormId.Value) ?? "";
                var qName = resolver.GetDisplayName(observation.QuestFormId.Value) ?? "";
                if (!string.IsNullOrEmpty(qEid)) meta["questEditorId"] = qEid;
                if (!string.IsNullOrEmpty(qName)) meta["questName"] = qName;
            }

            if (observation.TopicFormId.HasValue)
            {
                meta["topicFormId"] = $"0x{observation.TopicFormId.Value:X8}";
                var tEid = resolver.GetEditorId(observation.TopicFormId.Value) ?? "";
                var tName = resolver.GetDisplayName(observation.TopicFormId.Value) ?? "";
                if (string.IsNullOrEmpty(tName) &&
                    projection.DialogTopicObservations.TryGetValue(observation.TopicFormId.Value, out var topicObs))
                {
                    tName = topicObs.FullName ?? topicObs.DummyPrompt ?? "";
                }

                if (string.IsNullOrEmpty(tName))
                {
                    tName = observation.FirstPromptText ?? observation.FirstResponseText ?? "";
                }

                if (!string.IsNullOrEmpty(tEid)) meta["topicEditorId"] = tEid;
                if (!string.IsNullOrEmpty(tName)) meta["topicName"] = tName;
            }

            if (observation.SpeakerFormId.HasValue)
            {
                meta["speakerFormId"] = $"0x{observation.SpeakerFormId.Value:X8}";
                var sEid = resolver.GetEditorId(observation.SpeakerFormId.Value) ?? "";
                var sName = resolver.GetDisplayName(observation.SpeakerFormId.Value) ?? "";
                if (!string.IsNullOrEmpty(sEid)) meta["speakerEditorId"] = sEid;
                if (!string.IsNullOrEmpty(sName)) meta["speakerName"] = sName;
            }
        }
    }

    private static void ApplyDialogTopicSearchTextMetadata(
        CrossDumpRecordIndex index,
        CrossDumpSourceProjection projection)
    {
        if (projection.DialogTopicObservations.Count == 0)
        {
            return;
        }

        if (!index.RecordMetadata.TryGetValue("DialogTopic", out var metaMap))
        {
            metaMap = new Dictionary<uint, Dictionary<string, string>>();
            index.RecordMetadata["DialogTopic"] = metaMap;
        }

        foreach (var (formId, observation) in projection.DialogTopicObservations)
        {
            if (string.IsNullOrWhiteSpace(observation.SearchText))
            {
                continue;
            }

            if (!metaMap.TryGetValue(formId, out var meta))
            {
                meta = new Dictionary<string, string>();
                metaMap[formId] = meta;
            }

            meta["searchText"] = observation.SearchText;
        }
    }

    private static void FinalizeDialogueGroupLabels(
        CrossDumpRecordIndex index,
        Dictionary<uint, string> speakerLabels,
        Dictionary<uint, string> questLabels)
    {
        // Mirror of the original Aggregate's dialogue-label finalize step. Speaker / quest
        // labels may have been upgraded by later projections; earlier dialogue rows still
        // point at the stale label. Pull the speaker / quest FormID out of metadata and
        // overwrite with the canonical label.
        if (index.RecordGroups.TryGetValue("Dialogue_NPC", out var finalNpcGroups))
        {
            foreach (var (dialogueFormId, currentLabel) in finalNpcGroups.ToList())
            {
                if (index.RecordMetadata.TryGetValue("Dialogue", out var metas)
                    && metas.TryGetValue(dialogueFormId, out var m)
                    && m.TryGetValue("speakerFormId", out var spkHex)
                    && uint.TryParse(spkHex.Replace("0x", ""), NumberStyles.HexNumber, null, out var spkFid)
                    && speakerLabels.TryGetValue(spkFid, out var canonicalLabel)
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
                    && uint.TryParse(qstHex.Replace("0x", ""), NumberStyles.HexNumber, null, out var qstFid)
                    && questLabels.TryGetValue(qstFid, out var canonicalLabel)
                    && canonicalLabel != currentLabel)
                {
                    finalQuestGroups[dialogueFormId] = canonicalLabel;
                }
            }
        }
    }
}

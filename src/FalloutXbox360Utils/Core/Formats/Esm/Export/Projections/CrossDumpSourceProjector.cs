using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Builds a <see cref="CrossDumpSourceProjection" /> from a loaded
///     <see cref="SemanticSource" />. The projection captures every piece of state the
///     cross-dump aggregator + HTML writers read from a source, so the originating
///     <c>RecordCollection</c> can be dropped immediately afterwards.
/// </summary>
/// <remarks>
///     <para>
///         <b>Pass A reports.</b> Built here for every record type EXCEPT
///         <c>NPC</c>, <c>Key</c>, and <c>Container</c>. Those three need cross-source
///         enrichment indexes (placements + key-locked doors) that don't exist until every
///         source is projected — they're filled in by pass B inside the comparison pipeline.
///     </para>
///     <para>
///         <b>Cell + MapMarker reports</b> are built with an EMPTY virtual-cell canonical
///         FormID map. Within a single source this gives correct cell FormIDs for non-virtual
///         cells and correct door/marker lookups; the cross-source canonicalization that
///         remaps virtual cells to a shared FormID happens in the aggregator, not at projection
///         time. If that becomes a visible drift in a future fixture, the fix is to rebuild
///         affected Cell/MapMarker reports during pass B using the cross-source canon.
///     </para>
///     <para>
///         <b>Chronological invariant.</b> Per-source observations are captured in
///         <see cref="CrossDumpSourceProjection.WorldspaceObservations" /> /
///         <see cref="CrossDumpSourceProjection.CellGroupObservations" /> as ordered lists.
///         The aggregator must sort projections by build date and replay observations in
///         chronological order — this is what preserves worldspace rename history
///         ("Camp McCarran Tarmac → Camp McCarran (CampMcCarranWorld)") and the
///         ESM-authority gate for cell group assignment.
///     </para>
/// </remarks>
internal static class CrossDumpSourceProjector
{
    /// <summary>
    ///     Project a freshly-loaded <see cref="SemanticSource" /> into the lightweight
    ///     <see cref="CrossDumpSourceProjection" />. The source's <c>RecordCollection</c>
    ///     is read once but not modified — the caller decides when to release it.
    /// </summary>
    internal static CrossDumpSourceProjection Project(SemanticSource source)
    {
        var (buildDateUtc, dateSource) = CrossDumpBuildDate.Resolve(source.FilePath, source.MinidumpInfo);
        var isDmp = source.FilePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
        var shortName = Path.GetFileNameWithoutExtension(source.FilePath);
        var records = source.Records;
        var resolver = source.Resolver;

        // Cross-source skeleton lists. Projection iterates records exactly once.
        var cellSkeletons = BuildCellSkeletons(records.Cells);
        var npcSkeletons = records.Npcs.Select(npc => new NpcSkeleton(npc.FormId)).ToList();
        var keySkeletons = records.Keys.Select(key => new KeySkeleton(key.FormId)).ToList();
        var containerSkeletons = records.Containers
            .Select(container => new ContainerSkeleton(container.FormId))
            .ToList();
        var scriptSkeletons = BuildScriptSkeletons(records.Scripts);

        // Observations replayed in chronological order by the aggregator (NOT folded here).
        var worldspaceObservations = BuildWorldspaceObservations(records.Worldspaces);
        var cellGroupObservations = BuildCellGroupObservations(records.Cells);
        var dialogTopicSearchText = BuildDialogTopicSearchTextLookup(records.Dialogues);
        var dialogueObservations = BuildDialogueObservations(records.Dialogues);
        var dialogTopicObservations = BuildDialogTopicObservations(records.DialogTopics, dialogTopicSearchText);

        // Direct worldspace name lookup (covers the runtime-extracted DMP path the Resolver
        // misses — see CrossDumpAggregator.BuildWorldspaceNameLookup).
        var worldspaceNames = records.Worldspaces
            .Where(w => w.FormId != 0)
            .ToDictionary(
                w => w.FormId,
                w => new WorldspaceNameEntry(w.EditorId, w.FullName));

        // Per-source intra-source enrichment indexes for pass-A report building.
        var factionMembers = records.BuildFactionMembersIndex();
        var modToWeapon = records.BuildModToWeaponMap();
        // Empty virtual-cell canon: Cell/MapMarker reports built here resolve placements
        // within this source only. Cross-source canonicalization is a future improvement.
        var virtualCanon = new Dictionary<CellCoordinateKey, RealCellCandidate>();
        var placedReferenceLocations = CrossDumpPlacementIndexBuilder.BuildPlacedReferenceLocations(
            cellSkeletons,
            virtualCanon);

        // Pass A: build reports for every type whose report does NOT depend on cross-source
        // enrichment. NPC/Key/Container reports are filled in by pass B (Phase 5).
        var reportsByType = BuildPassAReports(
            records,
            resolver,
            factionMembers,
            modToWeapon,
            placedReferenceLocations);

        // Pass B will read these to build NPC/Key/Container reports with cross-source data.
        var lateEnrichment = new LateEnrichmentRecords
        {
            Npcs = records.Npcs,
            Keys = records.Keys,
            Containers = records.Containers
        };

        return new CrossDumpSourceProjection
        {
            FilePath = source.FilePath,
            ShortName = shortName,
            IsDmp = isDmp,
            BuildDateUtc = buildDateUtc,
            DateSource = dateSource,
            Resolver = resolver,
            MinidumpInfo = source.MinidumpInfo,
            ReportsByType = reportsByType,
            WorldspaceObservations = worldspaceObservations,
            CellGroupObservations = cellGroupObservations,
            DialogueObservations = dialogueObservations,
            DialogTopicObservations = dialogTopicObservations,
            WorldspaceNames = worldspaceNames,
            CellSkeletons = cellSkeletons,
            NpcSkeletons = npcSkeletons,
            KeySkeletons = keySkeletons,
            ContainerSkeletons = containerSkeletons,
            ScriptSkeletons = scriptSkeletons,
            LateEnrichment = lateEnrichment
        };
    }

    // ---------- Skeleton builders ----------

    private static List<CellSkeleton> BuildCellSkeletons(List<CellRecord> cells)
    {
        var skeletons = new List<CellSkeleton>(cells.Count);
        foreach (var cell in cells)
        {
            var placedObjects = cell.PlacedObjects
                .Select(PlacedObjectSkeleton.From)
                .ToList();

            skeletons.Add(new CellSkeleton
            {
                FormId = cell.FormId,
                EditorId = cell.EditorId,
                FullName = cell.FullName,
                WorldspaceFormId = cell.WorldspaceFormId,
                GridX = cell.GridX,
                GridY = cell.GridY,
                Flags = cell.Flags,
                IsVirtual = cell.IsVirtual,
                IsPersistentCell = cell.IsPersistentCell,
                IsUnresolvedBucket = cell.IsUnresolvedBucket,
                HasPersistentObjects = cell.HasPersistentObjects,
                PlacedObjects = placedObjects
            });
        }

        return skeletons;
    }

    private static List<ScriptSkeleton> BuildScriptSkeletons(List<ScriptRecord> scripts)
    {
        var skeletons = new List<ScriptSkeleton>(scripts.Count);
        foreach (var script in scripts)
        {
            // Distinct() here so the consumer side (BuildNpcScriptReferenceIndex) doesn't
            // need to re-dedup. Order is preserved as first-seen.
            var referencedObjects = script.ReferencedObjects.Distinct().ToList();
            skeletons.Add(new ScriptSkeleton
            {
                FormId = script.FormId,
                EditorId = script.EditorId,
                ScriptType = script.ScriptType,
                OwnerQuestFormId = script.OwnerQuestFormId,
                ReferencedObjects = referencedObjects
            });
        }

        return skeletons;
    }

    // ---------- Observation builders (load-order; chronological replay is the aggregator's job) ----------

    private static List<WorldspaceObservation> BuildWorldspaceObservations(
        List<WorldspaceRecord> worldspaces)
    {
        var observations = new List<WorldspaceObservation>(worldspaces.Count);
        foreach (var worldspace in worldspaces)
        {
            if (worldspace.FormId == 0)
            {
                continue;
            }

            observations.Add(new WorldspaceObservation(
                worldspace.FormId,
                worldspace.FullName,
                worldspace.EditorId));
        }

        return observations;
    }

    private static List<CellGroupObservation> BuildCellGroupObservations(List<CellRecord> cells)
    {
        var observations = new List<CellGroupObservation>(cells.Count);
        foreach (var cell in cells)
        {
            if (cell.IsUnresolvedBucket)
            {
                // Matches the existing aggregator's filter: unresolved buckets aren't grouped.
                continue;
            }

            observations.Add(new CellGroupObservation(
                cell.FormId,
                cell.IsInterior,
                cell.WorldspaceFormId,
                cell.GridX,
                cell.GridY));
        }

        return observations;
    }

    private static Dictionary<uint, DialogueObservation> BuildDialogueObservations(
        List<DialogueRecord> dialogues)
    {
        var map = new Dictionary<uint, DialogueObservation>(dialogues.Count);
        foreach (var dialogue in dialogues)
        {
            // Match CrossDumpAggregator's dialogue per-record loop: capture the first prompt
            // text or first response text as the fallback "topic name" candidate.
            var firstResponseText = dialogue.Responses
                .Select(response => response.Text)
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

            map[dialogue.FormId] = new DialogueObservation
            {
                FormId = dialogue.FormId,
                QuestFormId = dialogue.QuestFormId,
                SpeakerFormId = dialogue.SpeakerFormId,
                TopicFormId = dialogue.TopicFormId,
                FirstPromptText = dialogue.PromptText,
                FirstResponseText = firstResponseText
            };
        }

        return map;
    }

    private static Dictionary<uint, DialogTopicObservation> BuildDialogTopicObservations(
        List<DialogTopicRecord> topics,
        Dictionary<uint, string> searchTextByTopicFormId)
    {
        var map = new Dictionary<uint, DialogTopicObservation>(topics.Count);
        foreach (var topic in topics)
        {
            searchTextByTopicFormId.TryGetValue(topic.FormId, out var searchText);
            map[topic.FormId] = new DialogTopicObservation
            {
                FormId = topic.FormId,
                FullName = topic.FullName,
                DummyPrompt = topic.DummyPrompt,
                SearchText = searchText
            };
        }

        return map;
    }

    private static Dictionary<uint, string> BuildDialogTopicSearchTextLookup(
        IReadOnlyList<DialogueRecord> dialogues)
    {
        // Mirror of CrossDumpAggregator.BuildDialogTopicSearchTextLookup: aggregate prompt
        // + response text across all dialogue records that belong to each topic.
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

    // ---------- Pass-A report builder ----------

    private static Dictionary<string, Dictionary<uint, RecordReport>> BuildPassAReports(
        RecordCollection records,
        FormIdResolver resolver,
        Dictionary<uint, List<(uint FormId, string? Name)>> factionMembers,
        Dictionary<uint, List<(Models.Records.Item.WeaponRecord Weapon, WeaponModSlot Slot)>> modToWeapon,
        IReadOnlyDictionary<uint, PlacedReferenceLocation> placedReferenceLocations)
    {
        var reportsByType = new Dictionary<string, Dictionary<uint, RecordReport>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (typeName, formId, _, _, record) in RecordTextFormatter.EnumerateAll(records))
        {
            // Skip the three types whose reports need cross-source enrichment. Pass B
            // (inside CrossDumpComparisonPipeline) will fill these from LateEnrichmentRecords.
            if (string.Equals(typeName, "NPC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "Key", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(typeName, "Container", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Mirror the aggregator's unresolved-bucket cell filter.
            if (record is CellRecord { IsUnresolvedBucket: true })
            {
                continue;
            }

            var report = RecordTextFormatter.BuildReport(
                record,
                resolver,
                factionMembers,
                keyLockedDoors: null, // cross-source — not needed for non-Key reports
                modToWeapon,
                placedReferenceLocations,
                npcPlacements: null, // cross-source — not needed for non-NPC reports
                npcScriptReferences: null, // cross-source — not needed for non-NPC reports
                containerPlacements: null // cross-source — not needed for non-Container reports
            );
            if (report == null)
            {
                continue;
            }

            if (!reportsByType.TryGetValue(typeName, out var formIdMap))
            {
                formIdMap = new Dictionary<uint, RecordReport>();
                reportsByType[typeName] = formIdMap;
            }

            // First-wins on duplicate FormID (matches RecordTextFormatter.EnumerateAll's
            // enumeration order — within a single source, a FormID should only show up once
            // per type, but guard against rare duplicates the same way Dictionary.TryAdd does).
            formIdMap.TryAdd(formId, report);
        }

        return reportsByType;
    }
}

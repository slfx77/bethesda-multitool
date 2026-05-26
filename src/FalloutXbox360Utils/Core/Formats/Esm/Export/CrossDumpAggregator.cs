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
    /// <remarks>
    ///     <b>Thin shim around the projection pipeline.</b> Builds a
    ///     <see cref="Projections.CrossDumpSourceProjection" /> per input tuple and delegates
    ///     to <see cref="Projections.CrossDumpProjectionAggregator.AggregateFromProjections" />.
    ///     The 500-line legacy <c>RecordCollection</c>-based loop body has been removed —
    ///     the projection path is now the single implementation. Caller-provided cross-source
    ///     indexes are still honored (avoid rebuilding them); the <c>releaseInputRecords</c>
    ///     parameter is a no-op since the projection path discards the heavy
    ///     <see cref="Models.RecordCollection" /> per source naturally.
    /// </remarks>
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
        _ = releaseInputRecords; // Defunct: projection path releases sources naturally.

        // Project each input tuple. Construct a synthetic SemanticSource so the existing
        // CrossDumpSourceProjector path can run unchanged.
        var projections = new List<Projections.CrossDumpSourceProjection>(dumps.Count);
        foreach (var (filePath, records, resolver, info) in dumps)
        {
            var source = new Semantic.SemanticSource
            {
                FilePath = filePath,
                FileType = filePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                    ? Core.AnalysisFileType.Minidump
                    : Core.AnalysisFileType.EsmFile,
                Records = records,
                Resolver = resolver,
                MinidumpInfo = info
            };
            projections.Add(Projections.CrossDumpSourceProjector.Project(source));
        }

        // Build cross-source indexes from skeletons if the caller didn't pre-build them.
        var virtualCanon = VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(
            projections.Select(p => p.CellSkeletons));
        npcPlacementIndexes ??=
            CrossDumpPlacementIndexBuilder.BuildNpcPlacementIndexes(projections, virtualCanon);
        npcScriptReferenceIndexes ??=
            CrossDumpPlacementIndexBuilder.BuildNpcScriptReferenceIndexes(projections);
        var keyLockedDoorIndexes =
            CrossDumpPlacementIndexBuilder.BuildKeyLockedDoorIndexes(projections, virtualCanon);
        containerPlacementIndexes ??=
            CrossDumpPlacementIndexBuilder.BuildContainerPlacementIndexes(projections, virtualCanon);

        Projections.CrossDumpProjectionAggregator.BuildLatePassReports(
            projections, npcPlacementIndexes, npcScriptReferenceIndexes,
            keyLockedDoorIndexes, containerPlacementIndexes);
        Projections.CrossDumpProjectionAggregator.ReleaseLateEnrichment(projections);

        return Projections.CrossDumpProjectionAggregator.AggregateFromProjections(
            projections, virtualCanon, allowedTypes);
    }

    // ShouldIncludeType / BuildDialogTopicLookup / BuildDialogTopicSearchTextLookup /
    // AddSearchValue / ClearRecordLists / BuildPlacedReferenceLocations were private
    // helpers used only by the legacy aggregator loop body that was replaced by the
    // projection-path shim in the previous commit. They became unreachable when the body
    // was removed.

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

    // BuildVirtualCellCanonicalFormIds / TryGetVirtualCellCanonicalFormId /
    // RebaseVirtualCellReport / AddUpgradedVirtualCellForDump /
    // AppendVirtualCellAuditMetadata were private delegate forwarders to
    // VirtualCellCanonicalizer used only by the legacy aggregator body. CrossDumpProjection-
    // Aggregator now calls VirtualCellCanonicalizer directly.

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

    // PickFirstDialogueText was used only by the legacy aggregator body's dialogue
    // metadata fallback. Replaced by DialogueObservation.FirstPromptText /
    // FirstResponseText captured at projection time.

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

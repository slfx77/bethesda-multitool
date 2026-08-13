using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Writer-side companion to <see cref="QuestVariableProducerGate" />. The gate reports only
///     the consumer-side outcome ("suppressed: no proven writer"); this reporter explains WHY
///     each unsupported append-only channel ended up writer-less: the fate of every collected
///     evidence owner (suppressed as a consumer in the gate's fixed point, removed before the
///     gate, or never counted), and the fail-closed scan reason for every candidate writer INFO
///     that produced no evidence. Diagnostic-only: never adds evidence, never mutates records.
/// </summary>
internal static class QuestVariableWriterDiagnostics
{
    private const string Phase = "Sanitizing script-variable conditions";

    /// <summary>
    ///     <paramref name="preGateDialogues" /> must be the pre-gate snapshot of the dialogue
    ///     list (the gate removes suppressed consumer INFOs in place, and a writer can also be
    ///     a consumer). <paramref name="survivingFreshMappings" /> is the gate's post-fixed-point
    ///     view (SelectFreshMappings over the gate result) — a fresh mapping absent there is an
    ///     unsupported channel even when raw evidence was collected for it.
    /// </summary>
    public static void Report(
        IReadOnlyList<DialogueRecord> preGateDialogues,
        DialogueCombinePlan combinePlan,
        IReadOnlyList<QuestVariableRecoveryMapping> freshMappings,
        IReadOnlyList<QuestVariableProducerEvidence> collectedEvidence,
        IReadOnlyList<QuestVariableRecoveryMapping> survivingFreshMappings,
        IReadOnlyList<QuestVariableProducerGateDiagnostic> gateDiagnostics,
        IReadOnlyDictionary<uint, uint>? formIdAliases,
        NewVsOverrideClassifier classifier,
        MasterDialogueIndex masterIndex,
        IConversionProgressSink sink)
    {
        var surviving = survivingFreshMappings.ToHashSet();
        var unsupported = freshMappings.Where(mapping => !surviving.Contains(mapping)).ToList();
        if (unsupported.Count == 0)
        {
            return;
        }

        var unsupportedQuests = unsupported.Select(static mapping => mapping.SourceQuestFormId).ToHashSet();
        var newInfoFormIds = combinePlan.NewInfos.Select(static info => info.FormId).ToHashSet();
        var overlayFormIds = combinePlan.SharedInfoOverlays
            .Select(static overlay => overlay.PrototypeInfo.FormId)
            .ToHashSet();
        var preGateFormIds = preGateDialogues.Select(static info => info.FormId).ToHashSet();
        var suppressionsByRecord = gateDiagnostics.ToLookup(
            static diagnostic => (diagnostic.RecordType, diagnostic.RecordFormId));
        var ownersByMapping = collectedEvidence
            .GroupBy(static evidence => evidence.Mapping)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static evidence => evidence.Owner).Distinct().ToList());
        var candidateCountByMapping = new Dictionary<QuestVariableRecoveryMapping, int>();

        foreach (var info in preGateDialogues)
        {
            if (!ReferencesUnsupportedQuest(info, unsupportedQuests, formIdAliases))
            {
                continue;
            }

            var rejections = new List<ProducerScanRejection>();
            var wouldProve = QuestVariableBytecodeRemapper.CollectInfoProducerDiagnostics(
                info, unsupported, formIdAliases, rejections);
            foreach (var evidence in wouldProve)
            {
                candidateCountByMapping[evidence.Mapping] =
                    candidateCountByMapping.GetValueOrDefault(evidence.Mapping) + 1;
            }

            var route = ClassifyRoute(info.FormId, newInfoFormIds, overlayFormIds, classifier, masterIndex);
            if (wouldProve.Length > 0 && route != "new")
            {
                // This INFO's bytecode structurally proves the write, but the combine routed it
                // away from NewInfos, so production never saw the evidence and emission discards
                // the prototype result script (master bytes win).
                rejections.Add(new ProducerScanRejection(
                    "INFO", info.FormId, info.EditorId ?? $"INFO 0x{info.FormId:X8}",
                    "route-excluded-would-prove-write",
                    $"Bytecode proves {wouldProve.Length} mapped write(s), but combine route " +
                    $"'{route}' excludes this INFO from producer evidence and emission."));
            }
            else if (wouldProve.Length > 0
                     && SuppressionSummary(suppressionsByRecord, "INFO", info.FormId) is { } consumerFate)
            {
                // The production evidence path DID count this writer; the gate then removed the
                // INFO as a consumer of another unsupported channel, taking its evidence along —
                // the fixed-point cascade.
                rejections.Add(new ProducerScanRejection(
                    "INFO", info.FormId, info.EditorId ?? $"INFO 0x{info.FormId:X8}",
                    "writer-suppressed-as-consumer",
                    $"Bytecode proves {wouldProve.Length} mapped write(s), but the gate suppressed " +
                    $"this INFO as a consumer of: {consumerFate}."));
            }

            if (rejections.Count == 0)
            {
                continue;
            }

            var reasons = rejections
                .Select(static rejection => rejection.Reason)
                .Distinct()
                .ToList();
            var detail = string.Join(" | ", rejections.Select(static rejection =>
                $"{rejection.ScriptPath}: {rejection.Reason} — {rejection.Detail}"));
            sink.Warn(
                Phase,
                $"Writer candidate rejected (route={route}): {detail}",
                "INFO",
                info.FormId,
                "quest-variable.writer-rejected",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["route"] = route,
                    ["reasons"] = string.Join(",", reasons),
                    ["editor-id"] = info.EditorId,
                });
        }

        foreach (var mapping in unsupported)
        {
            var variable = string.IsNullOrWhiteSpace(mapping.SourceVariable.Name)
                ? $"ID {mapping.SourceVariable.Index}"
                : $"{mapping.SourceVariable.Name} (ID {mapping.SourceVariable.Index})";
            var owners = ownersByMapping.GetValueOrDefault(mapping);
            var ownerFates = owners is null or { Count: 0 }
                ? "No producer evidence was collected for this channel."
                : "Collected evidence owner(s): " + string.Join("; ", owners.Select(owner =>
                    $"{owner.RecordType} 0x{owner.SourceFormId:X8} ({owner.ScriptPath}) — {DescribeOwnerFate(owner, suppressionsByRecord, preGateFormIds)}"));
            sink.Warn(
                Phase,
                $"Append-only channel has no live writer: quest 0x{mapping.SourceQuestFormId:X8} " +
                $"local {variable} -> retained SCPT 0x{mapping.TargetScriptFormId:X8} " +
                $"ID {mapping.TargetVariable.Index}. {ownerFates} " +
                $"{candidateCountByMapping.GetValueOrDefault(mapping)} dialogue candidate(s) " +
                "carry a structurally provable write (see quest-variable.writer-rejected rows).",
                "SCPT",
                mapping.TargetScriptFormId,
                "quest-variable.channel-no-writer",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["source-quest"] = $"0x{mapping.SourceQuestFormId:X8}",
                    ["variable"] = mapping.SourceVariable.Name,
                    ["source-index"] = mapping.SourceVariable.Index.ToString(),
                    ["target-index"] = mapping.TargetVariable.Index.ToString(),
                    ["evidence-owner-count"] = (owners?.Count ?? 0).ToString(),
                });
        }
    }

    private static string DescribeOwnerFate(
        QuestVariableProducerOwner owner,
        ILookup<(string RecordType, uint RecordFormId), QuestVariableProducerGateDiagnostic> suppressionsByRecord,
        HashSet<uint> preGateFormIds)
    {
        if (SuppressionSummary(suppressionsByRecord, owner.RecordType, owner.SourceFormId) is { } fate)
        {
            return $"suppressed by the gate as a consumer of: {fate}";
        }

        if (owner.RecordType == "INFO" && !preGateFormIds.Contains(owner.SourceFormId))
        {
            return "removed before the gate ran (sanitizer/dedup/combine)";
        }

        return "not suppressed by the gate here — lost owner liveness elsewhere " +
               "(record list membership at fixed-point evaluation)";
    }

    private static string? SuppressionSummary(
        ILookup<(string RecordType, uint RecordFormId), QuestVariableProducerGateDiagnostic> suppressionsByRecord,
        string recordType,
        uint formId)
    {
        var rows = suppressionsByRecord[(recordType, formId)]
            .Select(static diagnostic =>
                $"{diagnostic.TargetVariableName ?? "?"} (ID {diagnostic.TargetVariableIndex}) " +
                $"@ SCPT 0x{diagnostic.TargetScriptFormId:X8}")
            .Distinct()
            .ToList();
        return rows.Count == 0 ? null : string.Join(", ", rows);
    }

    private static bool ReferencesUnsupportedQuest(
        DialogueRecord info,
        HashSet<uint> unsupportedQuests,
        IReadOnlyDictionary<uint, uint>? formIdAliases)
    {
        foreach (var script in info.ResultScripts)
        {
            foreach (var referenced in script.ReferencedObjects)
            {
                var resolved = formIdAliases is not null
                               && formIdAliases.TryGetValue(referenced, out var alias)
                    ? alias
                    : referenced;
                if (unsupportedQuests.Contains(resolved))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ClassifyRoute(
        uint formId,
        HashSet<uint> newInfoFormIds,
        HashSet<uint> overlayFormIds,
        NewVsOverrideClassifier classifier,
        MasterDialogueIndex masterIndex)
    {
        if (newInfoFormIds.Contains(formId))
        {
            return "new";
        }

        if (overlayFormIds.Contains(formId))
        {
            return "shared-overlay";
        }

        if (classifier.IsOverride(formId))
        {
            return masterIndex.TryGetInfo(formId, out _)
                ? "shared-overlay-no-op-suppressed"
                : "plan-suppressed-non-info-collision";
        }

        return "combine-suppressed";
    }
}

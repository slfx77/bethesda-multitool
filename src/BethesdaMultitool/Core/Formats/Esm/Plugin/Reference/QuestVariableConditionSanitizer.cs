using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     One pre-planning decision made for a CTDA that addresses a script variable.
/// </summary>
internal sealed record QuestVariableConditionDiagnostic(
    string Code,
    string RecordType,
    uint RecordFormId,
    string? EditorId,
    uint TargetFormId,
    uint VariableIndex,
    string? VariableName,
    uint? TargetScriptFormId,
    bool RecordSuppressed,
    string Message);

/// <summary>
///     Summary of quest/script-variable CTDA sanitation performed before record planning.
/// </summary>
internal sealed record QuestVariableConditionSanitizationResult(
    int SuppressedInfoCount,
    int SuppressedPackageCount,
    int RemappedConditionCount,
    int InvalidConditionCount,
    int UnresolvedTargetCount,
    int RetainedGetScriptVariableCount,
    IReadOnlyList<QuestVariableConditionDiagnostic> Diagnostics);

/// <summary>
///     Resolves the numeric variable IDs used by GetQuestVariable CTDAs against the script
///     table that the emitted plugin will actually leave attached to the quest.
/// </summary>
/// <remarks>
///     Master SCPT overrides are deliberately never emitted. A condition captured against
///     a prototype SCPT can therefore name a local-variable ID that is absent from the
///     retained retail SCPT, which makes the FNV runtime log "Unable to find variableID"
///     and can leave an INFO or PACK in an unsafe state. When that mismatch is proven, this
///     pass suppresses the entire newly-emitted INFO/PACK. It never drops only the CTDA:
///     doing so would widen the record to an unconditional dialogue line or AI package.
/// </remarks>
internal static class QuestVariableConditionSanitizer
{
    internal const ushort GetQuestVariableFunctionIndex = 79;
    internal const ushort GetScriptVariableFunctionIndex = 53;

    public static QuestVariableConditionSanitizationResult Apply(
        RecordCollection dmpRecords,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        ArgumentNullException.ThrowIfNull(dmpRecords);
        ArgumentNullException.ThrowIfNull(masterRecordsByFormId);

        var resolver = new VariableTableResolver(dmpRecords, masterRecordsByFormId, remapTable);
        var diagnostics = new List<QuestVariableConditionDiagnostic>();
        var suppressedInfos = 0;
        var suppressedPackages = 0;
        var remappedConditions = 0;
        var invalidConditions = 0;
        var unresolvedTargets = 0;
        var retainedGetScriptVariables = 0;

        for (var index = dmpRecords.Dialogues.Count - 1; index >= 0; index--)
        {
            var dialogue = dmpRecords.Dialogues[index];
            if (resolver.IsMasterAnchored("INFO", dialogue.FormId))
            {
                continue;
            }

            var outcome = SanitizeConditions(
                "INFO",
                dialogue.FormId,
                dialogue.EditorId,
                dialogue.Conditions,
                resolver,
                includeGetScriptVariableDiagnostics: false,
                diagnostics);

            remappedConditions += outcome.RemappedConditions;
            invalidConditions += outcome.InvalidConditions;
            unresolvedTargets += outcome.UnresolvedTargets;
            retainedGetScriptVariables += outcome.RetainedGetScriptVariables;

            if (outcome.SuppressRecord)
            {
                dmpRecords.Dialogues.RemoveAt(index);
                suppressedInfos++;
            }
            else if (outcome.RemappedConditions > 0)
            {
                dmpRecords.Dialogues[index] = dialogue with { Conditions = outcome.Conditions };
            }
        }

        for (var index = dmpRecords.Packages.Count - 1; index >= 0; index--)
        {
            var package = dmpRecords.Packages[index];
            if (resolver.IsMasterAnchored("PACK", package.FormId))
            {
                continue;
            }

            var outcome = SanitizeConditions(
                "PACK",
                package.FormId,
                package.EditorId,
                package.Conditions,
                resolver,
                includeGetScriptVariableDiagnostics: true,
                diagnostics);

            remappedConditions += outcome.RemappedConditions;
            invalidConditions += outcome.InvalidConditions;
            unresolvedTargets += outcome.UnresolvedTargets;
            retainedGetScriptVariables += outcome.RetainedGetScriptVariables;

            if (outcome.SuppressRecord)
            {
                dmpRecords.Packages.RemoveAt(index);
                suppressedPackages++;
            }
            else if (outcome.RemappedConditions > 0)
            {
                dmpRecords.Packages[index] = package with { Conditions = outcome.Conditions };
            }
        }

        diagnostics.Reverse();
        return new QuestVariableConditionSanitizationResult(
            suppressedInfos,
            suppressedPackages,
            remappedConditions,
            invalidConditions,
            unresolvedTargets,
            retainedGetScriptVariables,
            diagnostics);
    }

    private static RecordConditionOutcome SanitizeConditions(
        string recordType,
        uint recordFormId,
        string? editorId,
        IReadOnlyList<DialogueCondition> conditions,
        VariableTableResolver resolver,
        bool includeGetScriptVariableDiagnostics,
        List<QuestVariableConditionDiagnostic> diagnostics)
    {
        List<DialogueCondition>? patchedConditions = null;
        var suppressRecord = false;
        var remappedConditions = 0;
        var invalidConditions = 0;
        var unresolvedTargets = 0;
        var retainedGetScriptVariables = 0;

        for (var index = 0; index < conditions.Count; index++)
        {
            var condition = conditions[index];
            if (includeGetScriptVariableDiagnostics
                && condition.FunctionIndex == GetScriptVariableFunctionIndex)
            {
                retainedGetScriptVariables++;
                diagnostics.Add(new QuestVariableConditionDiagnostic(
                    "script-variable.owner-unresolved",
                    recordType,
                    recordFormId,
                    editorId,
                    condition.Parameter1,
                    condition.Parameter2,
                    null,
                    null,
                    false,
                    $"Retained GetScriptVariable target 0x{condition.Parameter1:X8}, " +
                    $"variable ID {condition.Parameter2}; reference-script ownership is not proven by this pass."));
                continue;
            }

            if (condition.FunctionIndex != GetQuestVariableFunctionIndex)
            {
                continue;
            }

            var decision = resolver.Resolve(condition.Parameter1, condition.Parameter2);
            switch (decision.Kind)
            {
                case VariableConditionDecisionKind.Valid:
                    break;

                case VariableConditionDecisionKind.Remap:
                    patchedConditions ??= [.. conditions];
                    patchedConditions[index] = condition with { Parameter2 = decision.RemappedIndex!.Value };
                    remappedConditions++;
                    diagnostics.Add(CreateDiagnostic(
                        "quest-variable.remapped",
                        recordType,
                        recordFormId,
                        editorId,
                        condition,
                        decision,
                        false,
                        $"Remapped GetQuestVariable ID {condition.Parameter2} -> " +
                        $"{decision.RemappedIndex.Value} by unique variable name/type match."));
                    break;

                case VariableConditionDecisionKind.Invalid:
                    suppressRecord = true;
                    invalidConditions++;
                    diagnostics.Add(CreateDiagnostic(
                        "quest-variable.record-suppressed",
                        recordType,
                        recordFormId,
                        editorId,
                        condition,
                        decision,
                        true,
                        "Suppressed the entire new record because its GetQuestVariable ID is absent from " +
                        "the retained target script and no unique name/type remap exists."));
                    break;

                case VariableConditionDecisionKind.Unresolved:
                    unresolvedTargets++;
                    diagnostics.Add(CreateDiagnostic(
                        "quest-variable.target-unresolved",
                        recordType,
                        recordFormId,
                        editorId,
                        condition,
                        decision,
                        false,
                        "Retained GetQuestVariable unchanged because the emitted quest/script binding " +
                        "could not be proven."));
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return new RecordConditionOutcome(
            patchedConditions ?? [.. conditions],
            suppressRecord,
            remappedConditions,
            invalidConditions,
            unresolvedTargets,
            retainedGetScriptVariables);
    }

    private static QuestVariableConditionDiagnostic CreateDiagnostic(
        string code,
        string recordType,
        uint recordFormId,
        string? editorId,
        DialogueCondition condition,
        VariableConditionDecision decision,
        bool recordSuppressed,
        string message)
    {
        return new QuestVariableConditionDiagnostic(
            code,
            recordType,
            recordFormId,
            editorId,
            condition.Parameter1,
            condition.Parameter2,
            decision.SourceVariable?.Name,
            decision.TargetScriptFormId,
            recordSuppressed,
            message);
    }

    private sealed record RecordConditionOutcome(
        List<DialogueCondition> Conditions,
        bool SuppressRecord,
        int RemappedConditions,
        int InvalidConditions,
        int UnresolvedTargets,
        int RetainedGetScriptVariables);

    private enum VariableConditionDecisionKind
    {
        Valid,
        Remap,
        Invalid,
        Unresolved
    }

    private sealed record VariableConditionDecision(
        VariableConditionDecisionKind Kind,
        ScriptVariableInfo? SourceVariable,
        uint? TargetScriptFormId,
        uint? RemappedIndex = null);

    private sealed class VariableTableResolver
    {
        private readonly IReadOnlyDictionary<uint, ParsedMainRecord> _masterRecords;
        private readonly IReadOnlyDictionary<uint, uint>? _remapTable;
        private readonly Dictionary<uint, QuestRecord> _dmpQuestsByFormId;
        private readonly Dictionary<uint, QuestRecord> _dmpQuestsByResolvedFormId;
        private readonly Dictionary<uint, ScriptRecord> _dmpScriptsByFormId;
        private readonly Dictionary<uint, ScriptRecord> _dmpScriptsByResolvedFormId;
        private readonly Dictionary<uint, IReadOnlyList<ScriptVariableInfo>> _masterVariablesByScript;

        public VariableTableResolver(
            RecordCollection dmpRecords,
            IReadOnlyDictionary<uint, ParsedMainRecord> masterRecords,
            IReadOnlyDictionary<uint, uint>? remapTable)
        {
            _masterRecords = masterRecords;
            _remapTable = remapTable;
            _dmpQuestsByFormId = BuildFirstByFormId(dmpRecords.Quests, static q => q.FormId);
            _dmpScriptsByFormId = BuildFirstByFormId(dmpRecords.Scripts, static s => s.FormId);
            _dmpQuestsByResolvedFormId = BuildFirstByResolvedFormId(
                dmpRecords.Quests,
                static q => q.FormId);
            _dmpScriptsByResolvedFormId = BuildFirstByResolvedFormId(
                dmpRecords.Scripts,
                static s => s.FormId);
            _masterVariablesByScript = masterRecords.Values
                .Where(static r => r.Header.Signature == "SCPT")
                .ToDictionary(
                    static r => r.Header.FormId,
                    static r => (IReadOnlyList<ScriptVariableInfo>)EsmScriptBlockReader.ReadScriptVariables(
                        r.Subrecords,
                        0,
                        r.Subrecords.Count));
        }

        public bool IsMasterAnchored(string signature, uint sourceFormId)
        {
            var resolved = ResolveAlias(sourceFormId);
            return _masterRecords.TryGetValue(resolved, out var record)
                   && record.Header.Signature == signature;
        }

        public VariableConditionDecision Resolve(uint sourceQuestFormId, uint sourceVariableIndex)
        {
            var resolvedQuestFormId = ResolveAlias(sourceQuestFormId);
            var sourceQuest = _dmpQuestsByFormId.GetValueOrDefault(sourceQuestFormId)
                              ?? _dmpQuestsByResolvedFormId.GetValueOrDefault(resolvedQuestFormId);
            var sourceScript = FindSourceScript(sourceQuest);
            var sourceVariables = sourceScript is { Variables.Count: > 0 }
                ? sourceScript.Variables
                : sourceQuest?.Variables;
            var sourceVariable = FindVariable(
                sourceVariables,
                sourceVariableIndex);

            if (!TryGetTargetVariableTable(
                    sourceQuest,
                    resolvedQuestFormId,
                    out var targetScriptFormId,
                    out var targetVariables))
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Unresolved,
                    sourceVariable,
                    targetScriptFormId);
            }

            var targetAtSameIndex = FindVariable(targetVariables, sourceVariableIndex);
            if (sourceVariable is null || string.IsNullOrWhiteSpace(sourceVariable.Name))
            {
                return targetAtSameIndex is not null
                    ? new VariableConditionDecision(
                        VariableConditionDecisionKind.Valid,
                        sourceVariable,
                        targetScriptFormId)
                    : new VariableConditionDecision(
                        VariableConditionDecisionKind.Invalid,
                        sourceVariable,
                        targetScriptFormId);
            }

            if (targetAtSameIndex is not null && VariablesMatch(sourceVariable, targetAtSameIndex))
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Valid,
                    sourceVariable,
                    targetScriptFormId);
            }

            var matchingTargets = targetVariables
                .Where(target => VariablesMatch(sourceVariable, target))
                .ToList();
            return matchingTargets.Count == 1
                ? new VariableConditionDecision(
                    VariableConditionDecisionKind.Remap,
                    sourceVariable,
                    targetScriptFormId,
                    matchingTargets[0].Index)
                : new VariableConditionDecision(
                    VariableConditionDecisionKind.Invalid,
                    sourceVariable,
                    targetScriptFormId);
        }

        private ScriptRecord? FindSourceScript(QuestRecord? sourceQuest)
        {
            if (sourceQuest?.Script is not > 0)
            {
                return null;
            }

            var sourceScriptFormId = sourceQuest.Script.Value;
            return _dmpScriptsByFormId.GetValueOrDefault(sourceScriptFormId)
                   ?? _dmpScriptsByResolvedFormId.GetValueOrDefault(ResolveAlias(sourceScriptFormId));
        }

        private bool TryGetTargetVariableTable(
            QuestRecord? sourceQuest,
            uint resolvedQuestFormId,
            out uint? targetScriptFormId,
            out IReadOnlyList<ScriptVariableInfo> targetVariables)
        {
            targetScriptFormId = null;
            targetVariables = [];

            // Planned QUST overrides retain the master's SCRI. A captured master-quest
            // model can point at a prototype-only SCPT, but that pointer is not serialized
            // by PlannedQustEncoder; validating against the DMP table would bless variable
            // IDs that the runtime can never resolve. Resolve master QUST -> master SCPT
            // first and do not fall through to the DMP script for a master-anchored quest.
            if (_masterRecords.TryGetValue(resolvedQuestFormId, out var masterQuest)
                && masterQuest.Header.Signature == "QUST")
            {
                var scri = masterQuest.Subrecords.FirstOrDefault(
                    static s => s.Signature == "SCRI" && s.Data.Length >= 4);
                if (scri is null || scri.DataAsFormId == 0)
                {
                    return false;
                }

                targetScriptFormId = ResolveAlias(scri.DataAsFormId);
                return TryGetMasterScriptVariables(targetScriptFormId.Value, out targetVariables);
            }

            if (sourceQuest?.Script is > 0)
            {
                var sourceScriptFormId = sourceQuest.Script.Value;
                var resolvedScriptFormId = ResolveAlias(sourceScriptFormId);

                // Existing SCPTs remain master-pure even when the DMP captured a semantic
                // script model at the same/aliased FormID.
                if (TryGetMasterScriptVariables(
                        resolvedScriptFormId,
                        out targetVariables))
                {
                    targetScriptFormId = resolvedScriptFormId;
                    return true;
                }

                // A genuinely-new SCPT is emitted before PACK/QUST/INFO and keeps the DMP
                // variable table (under a newly allocated FormID).
                var dmpScript = _dmpScriptsByFormId.GetValueOrDefault(sourceScriptFormId)
                                ?? _dmpScriptsByResolvedFormId.GetValueOrDefault(resolvedScriptFormId);
                if (dmpScript is not null)
                {
                    targetScriptFormId = resolvedScriptFormId;
                    targetVariables = dmpScript.Variables;
                    return true;
                }
            }

            return false;
        }

        private bool TryGetMasterScriptVariables(
            uint scriptFormId,
            out IReadOnlyList<ScriptVariableInfo> variables)
        {
            if (_masterRecords.TryGetValue(scriptFormId, out var scriptRecord)
                && scriptRecord.Header.Signature == "SCPT"
                && _masterVariablesByScript.TryGetValue(scriptFormId, out variables!))
            {
                return true;
            }

            variables = [];
            return false;
        }

        private uint ResolveAlias(uint formId)
        {
            if (_remapTable is null || formId == 0)
            {
                return formId;
            }

            var current = formId;
            var visited = new HashSet<uint>();
            while (visited.Add(current)
                   && _remapTable.TryGetValue(current, out var mapped)
                   && mapped != 0
                   && mapped != current)
            {
                current = mapped;
            }

            return current;
        }

        private Dictionary<uint, T> BuildFirstByResolvedFormId<T>(
            IEnumerable<T> records,
            Func<T, uint> formIdSelector)
        {
            var result = new Dictionary<uint, T>();
            foreach (var record in records)
            {
                var formId = formIdSelector(record);
                if (formId != 0)
                {
                    result.TryAdd(ResolveAlias(formId), record);
                }
            }

            return result;
        }

        private static Dictionary<uint, T> BuildFirstByFormId<T>(
            IEnumerable<T> records,
            Func<T, uint> formIdSelector)
        {
            var result = new Dictionary<uint, T>();
            foreach (var record in records)
            {
                var formId = formIdSelector(record);
                if (formId != 0)
                {
                    result.TryAdd(formId, record);
                }
            }

            return result;
        }

        private static ScriptVariableInfo? FindVariable(
            IReadOnlyList<ScriptVariableInfo>? variables,
            uint index)
        {
            return variables?.FirstOrDefault(variable => variable.Index == index);
        }

        private static bool VariablesMatch(ScriptVariableInfo source, ScriptVariableInfo target)
        {
            return source.Type == target.Type
                   && string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}

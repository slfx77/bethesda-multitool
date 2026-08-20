using System.Collections.Immutable;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Planner;

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
    string Message,
    IReadOnlyDictionary<string, string?> Metadata);

/// <summary>
///     Summary of quest/script-variable CTDA sanitation performed before record planning.
/// </summary>
internal sealed record QuestVariableConditionSanitizationResult(
    int SuppressedInfoCount,
    int SuppressedPackageCount,
    int SuppressedTerminalMenuItemCount,
    int SuppressedPrototypeDerivedInfoCount,
    int RemappedConditionCount,
    int InvalidConditionCount,
    int UnresolvedTargetCount,
    int RetainedGetScriptVariableCount,
    ImmutableArray<ScriptVariableAugmentation> ScriptVariableAugmentations,
    ImmutableArray<QuestVariableRecoveryMapping> VariableRecoveryMappings,
    IReadOnlyList<QuestVariableConditionDiagnostic> Diagnostics);

/// <summary>
///     Resolves numeric variable IDs used by GetQuestVariable and GetScriptVariable
///     CTDAs against the script table that the emitted plugin will actually leave attached
///     to the quest or referenced object's base.
/// </summary>
/// <remarks>
///     A condition captured against a prototype SCPT can name a local-variable ID that is
///     absent from the retained retail SCPT, which makes the FNV runtime log "Unable to find
///     variableID" and can leave an INFO or PACK in an unsafe state. When the captured
///     variable has complete lexical metadata, this pass reserves a fresh append-only ID,
///     schedules a byte-preserving augmentation of the retained retail script, and remaps
///     the condition to it. Missing metadata and ambiguous exact matches still suppress the
///     entire newly-emitted INFO/PACK or TERM menu item. GetScriptVariable additionally requires a proven
///     REFR/ACHR/ACRE -> NAME base -> SCRI chain; it remaps only by the same unique exact
///     name/type identity and otherwise suppresses the INFO/PACK/menu item. It never drops only the CTDA:
///     doing so would widen the owner to unconditional dialogue, AI, or terminal content.
/// </remarks>
internal static class QuestVariableConditionSanitizer
{
    internal const ushort GetQuestVariableFunctionIndex = 79;
    internal const ushort GetScriptVariableFunctionIndex = 53;

    private static readonly IReadOnlyDictionary<string, string?> EmptyMetadata =
        new Dictionary<string, string?>();

    public static QuestVariableConditionSanitizationResult Apply(
        RecordCollection dmpRecords,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? remapTable,
        bool sanitizeInfoRecords = true,
        bool sanitizePackageRecords = true,
        bool sanitizeTerminalRecords = true,
        IReadOnlySet<uint>? sanitizeMasterAnchoredInfoFormIds = null)
    {
        ArgumentNullException.ThrowIfNull(dmpRecords);
        ArgumentNullException.ThrowIfNull(masterRecordsByFormId);

        var resolver = new VariableTableResolver(dmpRecords, masterRecordsByFormId, remapTable);
        var scriptVariableResolver = new ScriptVariableConditionResolver(
            dmpRecords,
            masterRecordsByFormId,
            remapTable);
        IEnumerable<DialogueCondition> augmentationConditions = [];
        if (sanitizeInfoRecords)
        {
            augmentationConditions = dmpRecords.Dialogues
                .Where(dialogue => !resolver.IsMasterAnchored("INFO", dialogue.FormId)
                                   || sanitizeMasterAnchoredInfoFormIds?.Contains(dialogue.FormId) == true)
                .SelectMany(static dialogue => dialogue.Conditions);
        }

        if (sanitizePackageRecords)
        {
            augmentationConditions = augmentationConditions.Concat(
                dmpRecords.Packages
                    .Where(package => !resolver.IsMasterAnchored("PACK", package.FormId))
                    .SelectMany(static package => package.Conditions));
        }

        if (sanitizeTerminalRecords)
        {
            augmentationConditions = augmentationConditions.Concat(
                dmpRecords.Terminals
                    .Where(terminal => !resolver.IsMasterAnchored("TERM", terminal.FormId))
                    .SelectMany(static terminal => terminal.MenuItems)
                    .SelectMany(static item => item.Conditions));
        }

        resolver.PlanAugmentations(augmentationConditions);
        var diagnostics = new List<QuestVariableConditionDiagnostic>();
        var usedAugmentations = new HashSet<ScriptVariableAugmentation>();
        var usedMappings = new HashSet<QuestVariableRecoveryMapping>();
        var suppressedInfos = 0;
        var suppressedPackages = 0;
        var suppressedTerminalMenuItems = 0;
        var suppressedPrototypeDerivedInfos = 0;
        var remappedConditions = 0;
        var invalidConditions = 0;
        var unresolvedTargets = 0;
        var retainedGetScriptVariables = 0;
        var needsInfoMetadata = sanitizeInfoRecords && dmpRecords.Dialogues.Any(dialogue =>
            dialogue.Conditions.Any(condition =>
                condition.FunctionIndex is GetQuestVariableFunctionIndex or GetScriptVariableFunctionIndex));
        var topicsByFormId = needsInfoMetadata
            ? dmpRecords.DialogTopics.GroupBy(topic => topic.FormId)
                .ToDictionary(group => group.Key, group => group.First())
            : new Dictionary<uint, DialogTopicRecord>();
        var questsByFormId = needsInfoMetadata
            ? dmpRecords.Quests.GroupBy(quest => quest.FormId)
                .ToDictionary(group => group.Key, group => group.First())
            : new Dictionary<uint, QuestRecord>();
        var npcsByFormId = needsInfoMetadata
            ? dmpRecords.Npcs.GroupBy(npc => npc.FormId)
                .ToDictionary(group => group.Key, group => group.First())
            : new Dictionary<uint, NpcRecord>();

        for (var index = sanitizeInfoRecords ? dmpRecords.Dialogues.Count - 1 : -1; index >= 0; index--)
        {
            var dialogue = dmpRecords.Dialogues[index];
            var masterAnchored = resolver.IsMasterAnchored("INFO", dialogue.FormId);
            if (masterAnchored
                && sanitizeMasterAnchoredInfoFormIds?.Contains(dialogue.FormId) != true)
            {
                continue;
            }

            // INFO metadata can include every response string. Build it only when this
            // record can actually produce a quest-variable diagnostic; the common case
            // across thousands of ordinary INFOs must remain allocation-free.
            var metadata = dialogue.Conditions.Any(condition =>
                condition.FunctionIndex is GetQuestVariableFunctionIndex or GetScriptVariableFunctionIndex)
                ? BuildInfoMetadata(dialogue, topicsByFormId, questsByFormId, npcsByFormId)
                : EmptyMetadata;
            var diagnosticStart = diagnostics.Count;
            var outcome = SanitizeConditions(
                "INFO",
                dialogue.FormId,
                dialogue.EditorId,
                metadata,
                dialogue.Conditions,
                resolver,
                scriptVariableResolver,
                diagnostics);

            remappedConditions += outcome.RemappedConditions;
            invalidConditions += outcome.InvalidConditions;
            unresolvedTargets += outcome.UnresolvedTargets;
            retainedGetScriptVariables += outcome.RetainedGetScriptVariables;

            if (outcome.SuppressRecord)
            {
                if (masterAnchored)
                {
                    dmpRecords.Dialogues[index] = dialogue with
                    {
                        SuppressPrototypeDerivedDialogue = true
                    };
                    suppressedPrototypeDerivedInfos++;
                    RewritePrototypeDerivedDiagnostics(diagnostics, diagnosticStart);
                }
                else
                {
                    dmpRecords.Dialogues.RemoveAt(index);
                    suppressedInfos++;
                }
            }
            else
            {
                foreach (var augmentation in outcome.ScriptVariableAugmentations)
                {
                    usedAugmentations.Add(augmentation);
                }

                foreach (var mapping in outcome.VariableRecoveryMappings)
                {
                    usedMappings.Add(mapping);
                }

                if (outcome.RemappedConditions > 0)
                {
                    dmpRecords.Dialogues[index] = dialogue with { Conditions = outcome.Conditions };
                }
            }
        }

        for (var index = sanitizePackageRecords ? dmpRecords.Packages.Count - 1 : -1; index >= 0; index--)
        {
            var package = dmpRecords.Packages[index];
            if (resolver.IsMasterAnchored("PACK", package.FormId))
            {
                continue;
            }

            var metadata = package.Conditions.Any(condition =>
                condition.FunctionIndex is GetQuestVariableFunctionIndex or GetScriptVariableFunctionIndex)
                ? BuildBaseMetadata(package.EditorId)
                : EmptyMetadata;
            var outcome = SanitizeConditions(
                "PACK",
                package.FormId,
                package.EditorId,
                metadata,
                package.Conditions,
                resolver,
                scriptVariableResolver,
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
            else
            {
                foreach (var augmentation in outcome.ScriptVariableAugmentations)
                {
                    usedAugmentations.Add(augmentation);
                }

                foreach (var mapping in outcome.VariableRecoveryMappings)
                {
                    usedMappings.Add(mapping);
                }

                if (outcome.RemappedConditions > 0)
                {
                    dmpRecords.Packages[index] = package with { Conditions = outcome.Conditions };
                }
            }
        }

        for (var terminalIndex = sanitizeTerminalRecords ? dmpRecords.Terminals.Count - 1 : -1;
             terminalIndex >= 0;
             terminalIndex--)
        {
            var terminal = dmpRecords.Terminals[terminalIndex];
            if (resolver.IsMasterAnchored("TERM", terminal.FormId))
            {
                continue;
            }

            List<TerminalMenuItem>? patchedItems = null;
            for (var itemIndex = terminal.MenuItems.Count - 1; itemIndex >= 0; itemIndex--)
            {
                var item = terminal.MenuItems[itemIndex];
                var metadata = item.Conditions.Any(condition =>
                    condition.FunctionIndex is GetQuestVariableFunctionIndex or GetScriptVariableFunctionIndex)
                    ? BuildTerminalItemMetadata(terminal.EditorId, item, itemIndex)
                    : EmptyMetadata;
                var outcome = SanitizeConditions(
                    "TERM",
                    terminal.FormId,
                    terminal.EditorId,
                    metadata,
                    item.Conditions,
                    resolver,
                    scriptVariableResolver,
                    diagnostics);

                remappedConditions += outcome.RemappedConditions;
                invalidConditions += outcome.InvalidConditions;
                unresolvedTargets += outcome.UnresolvedTargets;
                retainedGetScriptVariables += outcome.RetainedGetScriptVariables;
                patchedItems ??= outcome.SuppressRecord || outcome.RemappedConditions > 0
                    ? [.. terminal.MenuItems]
                    : null;

                if (outcome.SuppressRecord)
                {
                    patchedItems!.RemoveAt(itemIndex);
                    suppressedTerminalMenuItems++;
                    continue;
                }

                foreach (var augmentation in outcome.ScriptVariableAugmentations)
                {
                    usedAugmentations.Add(augmentation);
                }

                foreach (var mapping in outcome.VariableRecoveryMappings)
                {
                    usedMappings.Add(mapping);
                }

                if (outcome.RemappedConditions > 0)
                {
                    patchedItems![itemIndex] = item with { Conditions = outcome.Conditions };
                }
            }

            if (patchedItems is not null)
            {
                dmpRecords.Terminals[terminalIndex] = terminal with { MenuItems = patchedItems };
            }
        }

        diagnostics.Reverse();
        var augmentations = usedAugmentations
            .OrderBy(static augmentation => augmentation.TargetScriptFormId)
            .ThenBy(static augmentation => augmentation.Variable.Index)
            .ThenBy(static augmentation => augmentation.Variable.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static augmentation => augmentation.Variable.Name, StringComparer.Ordinal)
            .ThenBy(static augmentation => augmentation.Variable.Type)
            .ToImmutableArray();
        var mappings = usedMappings
            .OrderBy(static mapping => mapping.SourceQuestFormId)
            .ThenBy(static mapping => mapping.SourceVariable.Index)
            .ThenBy(static mapping => mapping.TargetQuestFormId)
            .ThenBy(static mapping => mapping.TargetVariable.Index)
            .ThenBy(static mapping => mapping.SourceVariable.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static mapping => mapping.SourceVariable.Name, StringComparer.Ordinal)
            .ThenBy(static mapping => mapping.SourceVariable.Type)
            .ToImmutableArray();
        return new QuestVariableConditionSanitizationResult(
            suppressedInfos,
            suppressedPackages,
            suppressedTerminalMenuItems,
            suppressedPrototypeDerivedInfos,
            remappedConditions,
            invalidConditions,
            unresolvedTargets,
            retainedGetScriptVariables,
            augmentations,
            mappings,
            diagnostics);
    }

    private static void RewritePrototypeDerivedDiagnostics(
        List<QuestVariableConditionDiagnostic> diagnostics,
        int startIndex)
    {
        for (var index = startIndex; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            var metadata = new Dictionary<string, string?>(diagnostic.Metadata, StringComparer.Ordinal)
            {
                ["suppression-reason-code"] = diagnostic.Code,
                ["owner-disposition"] = "retail-overlay-retained",
                ["prototype-derived-disposition"] = "suppressed"
            };
            var code = diagnostic.Code.StartsWith("script-variable.", StringComparison.Ordinal)
                ? "script-variable.prototype-derived-info-suppressed"
                : "quest-variable.prototype-derived-info-suppressed";
            diagnostics[index] = diagnostic with
            {
                Code = code,
                RecordSuppressed = false,
                Message =
                "Suppressed only the prototype-derived INFO because its captured condition " +
                $"was unsafe ({diagnostic.Message}) The shared retail INFO overlay remains " +
                "master-byte authoritative.",
                Metadata = metadata
            };
        }
    }

    private static RecordConditionOutcome SanitizeConditions(
        string recordType,
        uint recordFormId,
        string? editorId,
        IReadOnlyDictionary<string, string?> recordMetadata,
        List<DialogueCondition> conditions,
        VariableTableResolver resolver,
        ScriptVariableConditionResolver? scriptVariableResolver,
        List<QuestVariableConditionDiagnostic> diagnostics)
    {
        List<DialogueCondition>? patchedConditions = null;
        var suppressRecord = false;
        var remappedConditions = 0;
        var invalidConditions = 0;
        var unresolvedTargets = 0;
        var retainedGetScriptVariables = 0;
        var augmentations = ImmutableArray.CreateBuilder<ScriptVariableAugmentation>();
        var mappings = ImmutableArray.CreateBuilder<QuestVariableRecoveryMapping>();

        for (var index = 0; index < conditions.Count; index++)
        {
            var condition = conditions[index];
            if (scriptVariableResolver is not null
                && condition.FunctionIndex == GetScriptVariableFunctionIndex)
            {
                var scriptDecision = scriptVariableResolver.Resolve(
                    condition.Parameter1,
                    condition.Parameter2);
                switch (scriptDecision.Kind)
                {
                    case ScriptVariableConditionResolutionKind.Valid:
                        retainedGetScriptVariables++;
                        break;

                    case ScriptVariableConditionResolutionKind.Remap:
                        patchedConditions ??= [.. conditions];
                        patchedConditions[index] = condition with
                        {
                            Parameter2 = scriptDecision.RemappedIndex!.Value
                        };
                        remappedConditions++;
                        diagnostics.Add(CreateScriptVariableDiagnostic(
                            recordType,
                            recordFormId,
                            editorId,
                            condition,
                            scriptDecision,
                            recordMetadata,
                            false));
                        break;

                    case ScriptVariableConditionResolutionKind.Invalid:
                        suppressRecord = true;
                        invalidConditions++;
                        diagnostics.Add(CreateScriptVariableDiagnostic(
                            recordType,
                            recordFormId,
                            editorId,
                            condition,
                            scriptDecision,
                            recordMetadata,
                            true));
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unhandled script-variable resolution kind '{scriptDecision.Kind}'.");
                }

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
                    mappings.Add(CreateRecoveryMapping(condition, decision));
                    diagnostics.Add(CreateDiagnostic(
                        "quest-variable.remapped",
                        recordType,
                        recordFormId,
                        editorId,
                        condition,
                        decision,
                        recordMetadata,
                        false,
                        $"Remapped GetQuestVariable ID {condition.Parameter2} -> " +
                        $"{decision.RemappedIndex.Value} by unique variable name/declaration match."));
                    break;

                case VariableConditionDecisionKind.Augment:
                {
                    patchedConditions ??= [.. conditions];
                    patchedConditions[index] = condition with { Parameter2 = decision.RemappedIndex!.Value };
                    remappedConditions++;
                    var augmentation = decision.Augmentation!;
                    augmentations.Add(augmentation);
                    mappings.Add(CreateRecoveryMapping(condition, decision));
                    var augmentationIdentity = ScriptVariableDeclarationIdentity.IsConcrete(
                        augmentation.DeclarationKind)
                        ? "exact captured name/declaration"
                        : "exact captured name/SLSD storage (fresh target local; no retail identity inferred)";
                    diagnostics.Add(CreateDiagnostic(
                        "quest-variable.augmented-and-remapped",
                        recordType,
                        recordFormId,
                        editorId,
                        condition,
                        decision,
                        recordMetadata,
                        false,
                        $"Reserved append-only variable ID {decision.RemappedIndex.Value} on retained " +
                        $"script 0x{decision.TargetScriptFormId!.Value:X8} as " +
                        $"'{augmentation.Variable.Name}' for the {augmentationIdentity} " +
                        $"and remapped GetQuestVariable ID {condition.Parameter2}."));
                    break;
                }

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
                        recordMetadata,
                        true,
                        "Suppressed the entire new record because its GetQuestVariable source metadata " +
                        "is missing, its exact name/declaration match is ambiguous, or its target cannot be " +
                        "safely augmented."));
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
                        recordMetadata,
                        false,
                        "Retained GetQuestVariable unchanged because the emitted quest/script binding " +
                        "could not be proven."));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unhandled quest-variable decision kind '{decision.Kind}'.");
            }
        }

        return new RecordConditionOutcome(
            patchedConditions ?? [.. conditions],
            suppressRecord,
            remappedConditions,
            invalidConditions,
            unresolvedTargets,
            retainedGetScriptVariables,
            augmentations.ToImmutable(),
            mappings.ToImmutable());
    }

    private static QuestVariableRecoveryMapping CreateRecoveryMapping(
        DialogueCondition condition,
        VariableConditionDecision decision)
    {
        if (decision.SourceVariable is null
            || !decision.TargetQuestFormId.HasValue
            || !decision.TargetScriptFormId.HasValue
            || !decision.RemappedIndex.HasValue
            || !decision.SourceDeclarationKind.HasValue)
        {
            throw new InvalidOperationException(
                "A remapped quest-variable condition is missing its exact source/target identity.");
        }

        var targetVariable = decision.Augmentation?.Variable
                             ?? decision.TargetVariable
                             ?? throw new InvalidOperationException(
                                 "A remapped quest-variable condition has no emitted target variable.");
        return new QuestVariableRecoveryMapping(
            condition.Parameter1,
            decision.TargetQuestFormId.Value,
            decision.TargetScriptFormId.Value,
            decision.SourceVariable,
            targetVariable,
            decision.SourceDeclarationKind.Value);
    }

    private static QuestVariableConditionDiagnostic CreateDiagnostic(
        string code,
        string recordType,
        uint recordFormId,
        string? editorId,
        DialogueCondition condition,
        VariableConditionDecision decision,
        IReadOnlyDictionary<string, string?> recordMetadata,
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
            message,
            BuildConditionMetadata(recordMetadata, condition, decision));
    }

    private static QuestVariableConditionDiagnostic CreateScriptVariableDiagnostic(
        string recordType,
        uint recordFormId,
        string? editorId,
        DialogueCondition condition,
        ScriptVariableConditionResolution decision,
        IReadOnlyDictionary<string, string?> recordMetadata,
        bool recordSuppressed)
    {
        var metadata = new Dictionary<string, string?>(
            BuildConditionMetadata(recordMetadata, condition, null),
            StringComparer.Ordinal)
        {
            ["condition-variable-name"] = decision.SourceVariable?.Name,
            ["condition-source-script-form-id"] = FormatFormId(decision.SourceScriptFormId),
            ["condition-target-script-form-id"] = FormatFormId(decision.TargetScriptFormId),
            ["condition-remapped-variable-index"] = decision.RemappedIndex?
                .ToString(CultureInfo.InvariantCulture)
        };
        foreach (var (key, value) in decision.Metadata)
        {
            metadata[key] = value;
        }

        return new QuestVariableConditionDiagnostic(
            decision.Code,
            recordType,
            recordFormId,
            editorId,
            condition.Parameter1,
            condition.Parameter2,
            decision.SourceVariable?.Name,
            decision.TargetScriptFormId,
            recordSuppressed,
            decision.Message,
            metadata);
    }

    private static Dictionary<string, string?> BuildInfoMetadata(
        DialogueRecord dialogue,
        Dictionary<uint, DialogTopicRecord> topicsByFormId,
        Dictionary<uint, QuestRecord> questsByFormId,
        Dictionary<uint, NpcRecord> npcsByFormId)
    {
        var (speakerScopeKind, speakerScopeFormId) = dialogue switch
        {
            { SpeakerFormId: { } exactSpeaker } => ("exact-npc", (uint?)exactSpeaker),
            { SpeakerFactionFormId: { } faction } => ("faction", (uint?)faction),
            { SpeakerRaceFormId: { } race } => ("race", (uint?)race),
            { SpeakerVoiceTypeFormId: { } voiceType } => ("voice-type", (uint?)voiceType),
            _ => (null, null)
        };
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["record-editor-id"] = dialogue.EditorId,
            ["info-prompt"] = dialogue.PromptText,
            ["info-topic-form-id"] = FormatFormId(dialogue.RawParentTopicFormId ?? dialogue.TopicFormId),
            ["info-quest-form-id"] = FormatFormId(dialogue.QuestFormId),
            ["info-speaker-form-id"] = FormatFormId(dialogue.SpeakerFormId),
            ["info-speaker-faction-form-id"] = FormatFormId(dialogue.SpeakerFactionFormId),
            ["info-speaker-race-form-id"] = FormatFormId(dialogue.SpeakerRaceFormId),
            ["info-speaker-voice-type-form-id"] = FormatFormId(dialogue.SpeakerVoiceTypeFormId),
            ["info-speaker-scope-kind"] = speakerScopeKind,
            ["info-speaker-scope-form-id"] = FormatFormId(speakerScopeFormId),
            ["info-response-count"] = dialogue.Responses.Count.ToString(CultureInfo.InvariantCulture)
        };

        var topicFormId = dialogue.RawParentTopicFormId ?? dialogue.TopicFormId;
        if (topicFormId is { } topicId && topicsByFormId.TryGetValue(topicId, out var topic))
        {
            metadata["info-topic-editor-id"] = topic.EditorId;
            metadata["info-topic-name"] = topic.FullName;
        }

        if (dialogue.QuestFormId is { } questId && questsByFormId.TryGetValue(questId, out var quest))
        {
            metadata["info-quest-editor-id"] = quest.EditorId;
            metadata["info-quest-name"] = quest.FullName;
        }

        if (dialogue.SpeakerFormId is { } speakerId && npcsByFormId.TryGetValue(speakerId, out var npc))
        {
            metadata["info-speaker-editor-id"] = npc.EditorId;
            metadata["info-speaker-name"] = npc.FullName;
        }

        for (var index = 0; index < dialogue.Responses.Count; index++)
        {
            var response = dialogue.Responses[index];
            var prefix = $"info-response-{index:D3}";
            metadata[$"{prefix}-number"] = response.ResponseNumber
                .ToString(CultureInfo.InvariantCulture);
            metadata[$"{prefix}-text"] = response.Text;
        }

        return metadata;
    }

    private static Dictionary<string, string?> BuildBaseMetadata(string? editorId)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["record-editor-id"] = editorId
        };
    }

    private static Dictionary<string, string?> BuildTerminalItemMetadata(
        string? editorId,
        TerminalMenuItem item,
        int itemIndex)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["record-editor-id"] = editorId,
            ["terminal-menu-index"] = itemIndex.ToString(
                CultureInfo.InvariantCulture),
            ["terminal-menu-text"] = item.Text,
            ["terminal-result-text"] = item.ResultText
        };
    }

    private static Dictionary<string, string?> BuildConditionMetadata(
        IReadOnlyDictionary<string, string?> recordMetadata,
        DialogueCondition condition,
        VariableConditionDecision? decision)
    {
        string? sourceIdentityProof = null;
        if (decision?.SourceDeclarationKind is { } kind)
        {
            sourceIdentityProof = ScriptVariableDeclarationIdentity.IsConcrete(kind)
                ? "same-dump-sctx-exact"
                : "same-dump-slsd-scvr-storage-only";
        }

        var metadata = new Dictionary<string, string?>(recordMetadata, StringComparer.Ordinal)
        {
            ["condition-target-form-id"] = FormatFormId(condition.Parameter1),
            ["condition-variable-index"] = condition.Parameter2
                .ToString(CultureInfo.InvariantCulture),
            ["condition-variable-name"] = decision?.SourceVariable?.Name,
            ["condition-target-script-form-id"] = FormatFormId(decision?.TargetScriptFormId),
            ["condition-remapped-variable-index"] = decision?.RemappedIndex?
                .ToString(CultureInfo.InvariantCulture),
            ["condition-augmented-variable-name"] = decision?.Augmentation?.Variable.Name,
            ["condition-variable-declaration-kind"] = decision?.SourceDeclarationKind?.ToString(),
            ["condition-source-identity-proof"] = sourceIdentityProof
        };
        return metadata;
    }

    private static string? FormatFormId(uint? formId)
    {
        return formId.HasValue ? $"0x{formId.Value:X8}" : null;
    }

    private sealed record RecordConditionOutcome(
        List<DialogueCondition> Conditions,
        bool SuppressRecord,
        int RemappedConditions,
        int InvalidConditions,
        int UnresolvedTargets,
        int RetainedGetScriptVariables,
        ImmutableArray<ScriptVariableAugmentation> ScriptVariableAugmentations,
        ImmutableArray<QuestVariableRecoveryMapping> VariableRecoveryMappings);

    private enum VariableConditionDecisionKind
    {
        Valid,
        Remap,
        Augment,
        Invalid,
        Unresolved
    }

    private sealed record VariableConditionDecision(
        VariableConditionDecisionKind Kind,
        ScriptVariableInfo? SourceVariable,
        uint? TargetScriptFormId,
        uint? TargetQuestFormId = null,
        ScriptVariableInfo? TargetVariable = null,
        uint? RemappedIndex = null,
        ScriptVariableAugmentation? Augmentation = null,
        ScriptVariableDeclarationKind? SourceDeclarationKind = null);

    private readonly record struct ScriptVariableIdentity(
        uint TargetScriptFormId,
        string Name,
        byte Type,
        ScriptVariableDeclarationKind DeclarationKind);

    private sealed class ScriptVariableIdentityComparer : IEqualityComparer<ScriptVariableIdentity>
    {
        public static ScriptVariableIdentityComparer Instance { get; } = new();

        public bool Equals(ScriptVariableIdentity x, ScriptVariableIdentity y)
        {
            return x.TargetScriptFormId == y.TargetScriptFormId
                   && x.Type == y.Type
                   && x.DeclarationKind == y.DeclarationKind
                   && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(ScriptVariableIdentity obj)
        {
            return HashCode.Combine(
                obj.TargetScriptFormId,
                obj.Type,
                obj.DeclarationKind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
        }
    }

    private sealed class VariableTableResolver
    {
        private readonly Dictionary<uint, QuestRecord> _dmpQuestsByFormId;
        private readonly Dictionary<uint, QuestRecord> _dmpQuestsByResolvedFormId;
        private readonly Dictionary<uint, List<ScriptRecord>> _dmpScriptsByFormId;
        private readonly Dictionary<uint, List<ScriptRecord>> _dmpScriptsByResolvedFormId;
        private readonly IReadOnlyDictionary<uint, ParsedMainRecord> _masterRecords;
        private readonly Dictionary<uint, string?> _masterSourceTextByScript;
        private readonly Dictionary<uint, IReadOnlyList<ScriptVariableInfo>> _masterVariablesByScript;

        private readonly Dictionary<ScriptVariableIdentity, ScriptVariableAugmentation>
            _plannedAugmentations = new(ScriptVariableIdentityComparer.Instance);

        private readonly IReadOnlyDictionary<uint, uint>? _remapTable;
        private readonly Dictionary<uint, List<RuntimeScriptData>> _runtimeScriptsByFormId = [];
        private readonly Dictionary<uint, List<RuntimeScriptData>> _runtimeScriptsByResolvedFormId = [];

        public VariableTableResolver(
            RecordCollection dmpRecords,
            IReadOnlyDictionary<uint, ParsedMainRecord> masterRecords,
            IReadOnlyDictionary<uint, uint>? remapTable)
        {
            _masterRecords = masterRecords;
            _remapTable = remapTable;
            _dmpQuestsByFormId = BuildFirstByFormId(dmpRecords.Quests, static q => q.FormId);
            _dmpScriptsByFormId = BuildScriptsByFormId(dmpRecords.Scripts);
            _dmpQuestsByResolvedFormId = BuildFirstByResolvedFormId(
                dmpRecords.Quests,
                static q => q.FormId);
            _dmpScriptsByResolvedFormId = BuildScriptsByResolvedFormId(dmpRecords.Scripts);
            foreach (var script in dmpRecords.RuntimeScripts.Where(static script => script.FormId != 0))
            {
                AddRuntimeScript(_runtimeScriptsByFormId, script.FormId, script);
                AddRuntimeScript(
                    _runtimeScriptsByResolvedFormId,
                    ResolveAlias(script.FormId),
                    script);
            }

            _masterVariablesByScript = masterRecords.Values
                .Where(static r => r.Header.Signature == "SCPT")
                .ToDictionary(
                    static r => r.Header.FormId,
                    static r => (IReadOnlyList<ScriptVariableInfo>)EsmScriptBlockReader.ReadScriptVariables(
                        r.Subrecords,
                        0,
                        r.Subrecords.Count));
            _masterSourceTextByScript = masterRecords.Values
                .Where(static r => r.Header.Signature == "SCPT")
                .ToDictionary(
                    static r => r.Header.FormId,
                    static r => ReadSingleSourceText(r));
        }

        public bool IsMasterAnchored(string signature, uint sourceFormId)
        {
            var resolved = ResolveAlias(sourceFormId);
            return _masterRecords.TryGetValue(resolved, out var record)
                   && record.Header.Signature == signature;
        }

        public void PlanAugmentations(IEnumerable<DialogueCondition> conditions)
        {
            ArgumentNullException.ThrowIfNull(conditions);

            var candidates = conditions
                .Where(static condition => condition.FunctionIndex == GetQuestVariableFunctionIndex)
                .Select(condition => ResolveCore(
                    condition.Parameter1,
                    condition.Parameter2,
                    true))
                .Where(static decision =>
                    decision.Kind == VariableConditionDecisionKind.Augment
                    && decision.SourceVariable is { Name: not null }
                    && decision.TargetScriptFormId.HasValue
                    && decision.SourceDeclarationKind.HasValue)
                .OrderBy(static decision => decision.TargetScriptFormId!.Value)
                .ThenBy(static decision => decision.SourceVariable!.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static decision => decision.SourceVariable!.Name, StringComparer.Ordinal)
                .ThenBy(static decision => decision.SourceVariable!.Type)
                .ThenBy(static decision => decision.SourceVariable!.Index)
                .ToList();
            var greatestIndexByScript = new Dictionary<uint, uint>();
            var reservedNamesByScript = new Dictionary<uint, HashSet<string>>();

            foreach (var candidate in candidates)
            {
                var targetScriptFormId = candidate.TargetScriptFormId!.Value;
                var sourceVariable = candidate.SourceVariable!;
                var identity = new ScriptVariableIdentity(
                    targetScriptFormId,
                    sourceVariable.Name!,
                    sourceVariable.Type,
                    candidate.SourceDeclarationKind!.Value);
                if (_plannedAugmentations.ContainsKey(identity))
                {
                    continue;
                }

                if (!greatestIndexByScript.TryGetValue(targetScriptFormId, out var greatestIndex))
                {
                    greatestIndex = _masterVariablesByScript[targetScriptFormId]
                        .Select(static variable => variable.Index)
                        .DefaultIfEmpty(0u)
                        .Max();
                }

                if (!reservedNamesByScript.TryGetValue(targetScriptFormId, out var reservedNames))
                {
                    reservedNames = new HashSet<string>(
                        _masterVariablesByScript[targetScriptFormId]
                            .Select(static variable => variable.Name)
                            .Where(static name => !string.IsNullOrWhiteSpace(name))
                            .Select(static name => name!),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var planned in _plannedAugmentations.Values.Where(augmentation =>
                                 augmentation.TargetScriptFormId == targetScriptFormId))
                    {
                        reservedNames.Add(planned.Variable.Name!);
                    }

                    reservedNamesByScript.Add(targetScriptFormId, reservedNames);
                }

                // CTDA stores the local ID as UInt32, but every SCDA ref.variable operand
                // stores it as UInt16.  Reserving an ID above that range would produce a
                // perfectly serializable SLSD/CTDA pair that no producer bytecode can ever
                // address, so the executable representation is the authoritative limit.
                if (greatestIndex >= ushort.MaxValue)
                {
                    throw new InvalidDataException(
                        $"Cannot append quest variable '{sourceVariable.Name}' to retained script " +
                        $"0x{targetScriptFormId:X8}: its UInt16 SCDA variable-ID range is exhausted.");
                }

                var allocatedIndex = greatestIndex + 1;
                greatestIndexByScript[targetScriptFormId] = allocatedIndex;
                var allocatedName = AllocateVariableName(sourceVariable.Name!, reservedNames);
                _plannedAugmentations.Add(
                    identity,
                    new ScriptVariableAugmentation(
                        targetScriptFormId,
                        new ScriptVariableInfo(
                            allocatedIndex,
                            allocatedName,
                            sourceVariable.Type),
                        candidate.SourceDeclarationKind.Value));
            }
        }

        private static string AllocateVariableName(string sourceName, HashSet<string> reservedNames)
        {
            if (reservedNames.Add(sourceName))
            {
                return sourceName;
            }

            var recoveryStem = sourceName + "_DmpRecovered";
            if (reservedNames.Add(recoveryStem))
            {
                return recoveryStem;
            }

            var suffix = 2;
            while (true)
            {
                var candidate = recoveryStem + suffix.ToString(
                    CultureInfo.InvariantCulture);
                if (reservedNames.Add(candidate))
                {
                    return candidate;
                }

                suffix++;
            }
        }

        public VariableConditionDecision Resolve(uint sourceQuestFormId, uint sourceVariableIndex)
        {
            return ResolveCore(
                sourceQuestFormId,
                sourceVariableIndex,
                false);
        }

        private VariableConditionDecision ResolveCore(
            uint sourceQuestFormId,
            uint sourceVariableIndex,
            bool allowUnplannedAugmentation)
        {
            var resolvedQuestFormId = ResolveAlias(sourceQuestFormId);
            var sourceQuest = _dmpQuestsByFormId.GetValueOrDefault(sourceQuestFormId)
                              ?? _dmpQuestsByResolvedFormId.GetValueOrDefault(resolvedQuestFormId);
            var sourceScript = FindSourceScript(sourceQuest, out var sourceScriptAmbiguous);
            var runtimeSourceScript = FindSourceRuntimeScript(
                sourceQuest,
                sourceQuest?.Script is { } sourceScriptFormId
                && _dmpScriptsByFormId.ContainsKey(sourceScriptFormId),
                out var runtimeMetadataInvalid);
            var sourceVariables = sourceQuest?.Variables;
            if (runtimeSourceScript is not null)
            {
                sourceVariables = runtimeSourceScript.Variables;
            }
            else if (sourceScript is { Variables.Count: > 0 })
            {
                sourceVariables = sourceScript.Variables;
            }

            var sourceVariablesAtIndex = sourceVariables?
                .Where(variable => variable.Index == sourceVariableIndex)
                .ToList() ?? [];
            var sourceVariable = sourceVariablesAtIndex.Count == 1
                ? sourceVariablesAtIndex[0]
                : null;
            var sourceText = ScriptVariableDeclarationIdentity.TryGetAcceptedDeclarationSourceText(
                sourceScript,
                runtimeSourceScript,
                out var acceptedSourceText)
                ? acceptedSourceText
                : null;

            var targetResolved = TryGetTargetVariableTable(
                sourceQuest,
                sourceScript,
                runtimeSourceScript,
                resolvedQuestFormId,
                out var targetScriptFormId,
                out var targetVariables,
                out var targetSourceText,
                out var targetIsRetainedMasterScript);
            if (sourceScriptAmbiguous || runtimeMetadataInvalid)
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Invalid,
                    null,
                    targetScriptFormId,
                    resolvedQuestFormId);
            }

            if (!targetResolved)
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Unresolved,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId);
            }

            var targetAtSameIndex = targetVariables
                .Where(variable => variable.Index == sourceVariableIndex)
                .ToList();
            if (sourceVariable is null || string.IsNullOrWhiteSpace(sourceVariable.Name))
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Invalid,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId);
            }

            var sourceKind = TryGetDeclarationKind(sourceVariable, sourceText, out var concreteSourceKind)
                ? concreteSourceKind
                : ScriptVariableDeclarationIdentity.FromStorage(sourceVariable);

            if (targetAtSameIndex.Count > 1)
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Invalid,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId,
                    SourceDeclarationKind: sourceKind);
            }

            // A non-concrete source identity or a non-retained target only supports the serialized
            // identity check; a concrete identity against a retained master script gets the full match.
            var sameIndexIdentityMatches = targetAtSameIndex.Count == 1
                                           && (!ScriptVariableDeclarationIdentity.IsConcrete(sourceKind) ||
                                               !targetIsRetainedMasterScript
                                               ? SameSerializedIdentity(sourceVariable, targetAtSameIndex[0])
                                               : VariablesMatch(sourceVariable, sourceKind, targetAtSameIndex[0],
                                                   targetSourceText));
            if (sameIndexIdentityMatches)
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Valid,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId,
                    targetAtSameIndex[0],
                    SourceDeclarationKind: sourceKind);
            }

            var matchingTargets = targetVariables
                .Where(target => VariablesMatch(sourceVariable, sourceKind, target, targetSourceText))
                .ToList();
            if (matchingTargets.Count == 1)
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Remap,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId,
                    matchingTargets[0],
                    matchingTargets[0].Index,
                    SourceDeclarationKind: sourceKind);
            }

            if (matchingTargets.Count > 1 || !targetIsRetainedMasterScript)
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Invalid,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId);
            }

            var identity = new ScriptVariableIdentity(
                targetScriptFormId!.Value,
                sourceVariable.Name,
                sourceVariable.Type,
                sourceKind);
            if (_plannedAugmentations.TryGetValue(identity, out var augmentation))
            {
                return new VariableConditionDecision(
                    VariableConditionDecisionKind.Augment,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId,
                    augmentation.Variable,
                    augmentation.Variable.Index,
                    augmentation,
                    sourceKind);
            }

            return allowUnplannedAugmentation
                ? new VariableConditionDecision(
                    VariableConditionDecisionKind.Augment,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId,
                    SourceDeclarationKind: sourceKind)
                : new VariableConditionDecision(
                    VariableConditionDecisionKind.Invalid,
                    sourceVariable,
                    targetScriptFormId,
                    resolvedQuestFormId);
        }

        private ScriptRecord? FindSourceScript(QuestRecord? sourceQuest, out bool ambiguous)
        {
            ambiguous = false;
            if (sourceQuest?.Script is not > 0)
            {
                return null;
            }

            var sourceScriptFormId = sourceQuest.Script.Value;
            var candidates = _dmpScriptsByFormId.GetValueOrDefault(sourceScriptFormId)
                             ?? _dmpScriptsByResolvedFormId.GetValueOrDefault(
                                 ResolveAlias(sourceScriptFormId));
            if (candidates is null || candidates.Count == 0)
            {
                return null;
            }

            var first = candidates[0];
            ambiguous = candidates.Skip(1).Any(candidate =>
                !ScriptVariableDeclarationIdentity.TablesEquivalent(
                    first.Variables,
                    first.SourceText,
                    candidate.Variables,
                    candidate.SourceText)
                || first.SourceTextCorrespondenceStatus
                != candidate.SourceTextCorrespondenceStatus
                || first.IsIncompleteExecutableBundle
                != candidate.IsIncompleteExecutableBundle);
            return ambiguous ? null : first;
        }

        private RuntimeScriptData? FindSourceRuntimeScript(
            QuestRecord? sourceQuest,
            bool sourceScriptIsRaw,
            out bool metadataInvalid)
        {
            metadataInvalid = false;
            if (sourceQuest?.Script is not > 0)
            {
                return null;
            }

            var sourceScriptFormId = sourceQuest.Script.Value;
            var resolvedScriptFormId = ResolveAlias(sourceScriptFormId);
            var candidates = _runtimeScriptsByFormId.GetValueOrDefault(sourceScriptFormId);
            if (candidates is null && !sourceScriptIsRaw)
            {
                candidates = _runtimeScriptsByResolvedFormId.GetValueOrDefault(resolvedScriptFormId);
            }

            if (candidates is null)
            {
                return null;
            }

            // Runtime Script objects are source evidence only. A partial list walk or two
            // incompatible same-dump objects must not be papered over with a fragment SCPT:
            // either case makes the captured numeric local's identity unprovable.
            if (candidates.Count == 0
                || candidates.Any(static candidate => !candidate.VariablesComplete))
            {
                metadataInvalid = true;
                return null;
            }

            var first = candidates[0];
            if (candidates.Skip(1).Any(candidate =>
                    !ScriptRuntimeMerger.RuntimeDataEquivalent(first, candidate)))
            {
                metadataInvalid = true;
                return null;
            }

            return first;
        }

        private bool TryGetTargetVariableTable(
            QuestRecord? sourceQuest,
            ScriptRecord? sourceScript,
            RuntimeScriptData? runtimeSourceScript,
            uint resolvedQuestFormId,
            out uint? targetScriptFormId,
            out IReadOnlyList<ScriptVariableInfo> targetVariables,
            out string? targetSourceText,
            out bool targetIsRetainedMasterScript)
        {
            targetScriptFormId = null;
            targetVariables = [];
            targetSourceText = null;
            targetIsRetainedMasterScript = false;

            // Planned QUST overrides retain the master's SCRI. A captured master-quest
            // model can point at a prototype-only SCPT, but that pointer is not serialized
            // by PlannedQustEncoder; validating against the DMP table would bless variable
            // IDs that the runtime can never resolve. Resolve master QUST -> master SCPT
            // first and do not fall through to the DMP script for a master-anchored quest.
            if (_masterRecords.TryGetValue(resolvedQuestFormId, out var masterQuest)
                && masterQuest.Header.Signature == "QUST")
            {
                var scri =
                    masterQuest.Subrecords.FirstOrDefault(static s => s.Signature == "SCRI" && s.Data.Length >= 4);
                if (scri is null || scri.DataAsFormId == 0)
                {
                    return false;
                }

                targetScriptFormId = ResolveAlias(scri.DataAsFormId);
                targetIsRetainedMasterScript = TryGetMasterScriptVariables(
                    targetScriptFormId.Value,
                    out targetVariables);
                if (targetIsRetainedMasterScript)
                {
                    _masterSourceTextByScript.TryGetValue(targetScriptFormId.Value, out targetSourceText);
                }

                return targetIsRetainedMasterScript;
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
                    targetIsRetainedMasterScript = true;
                    _masterSourceTextByScript.TryGetValue(resolvedScriptFormId, out targetSourceText);
                    return true;
                }

                // A genuinely-new SCPT is emitted before PACK/QUST/INFO and keeps the DMP
                // variable table (under a newly allocated FormID).
                if (sourceScript is not null)
                {
                    targetScriptFormId = resolvedScriptFormId;
                    targetVariables = sourceScript.Variables;
                    ScriptVariableDeclarationIdentity.TryGetAcceptedDeclarationSourceText(
                        sourceScript,
                        runtimeSourceScript,
                        out targetSourceText);
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

        private static Dictionary<uint, List<ScriptRecord>> BuildScriptsByFormId(
            IEnumerable<ScriptRecord> scripts)
        {
            var result = new Dictionary<uint, List<ScriptRecord>>();
            foreach (var script in scripts.Where(static script => script.FormId != 0))
            {
                AddScript(result, script.FormId, script);
            }

            return result;
        }

        private Dictionary<uint, List<ScriptRecord>> BuildScriptsByResolvedFormId(
            IEnumerable<ScriptRecord> scripts)
        {
            var result = new Dictionary<uint, List<ScriptRecord>>();
            foreach (var script in scripts.Where(static script => script.FormId != 0))
            {
                AddScript(result, ResolveAlias(script.FormId), script);
            }

            return result;
        }

        private static void AddScript(
            Dictionary<uint, List<ScriptRecord>> index,
            uint formId,
            ScriptRecord script)
        {
            if (!index.TryGetValue(formId, out var scripts))
            {
                scripts = [];
                index.Add(formId, scripts);
            }

            scripts.Add(script);
        }

        private static void AddRuntimeScript(
            Dictionary<uint, List<RuntimeScriptData>> index,
            uint formId,
            RuntimeScriptData script)
        {
            if (!index.TryGetValue(formId, out var scripts))
            {
                scripts = [];
                index.Add(formId, scripts);
            }

            scripts.Add(script);
        }

        private static bool VariablesMatch(
            ScriptVariableInfo source,
            ScriptVariableDeclarationKind sourceKind,
            ScriptVariableInfo target,
            string? targetSourceText)
        {
            return source.Type == target.Type
                   && string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                   && TryGetDeclarationKind(target, targetSourceText, out var targetKind)
                   && ScriptVariableDeclarationIdentity.KindsMatchExact(sourceKind, targetKind);
        }

        private static bool SameSerializedIdentity(
            ScriptVariableInfo source,
            ScriptVariableInfo target)
        {
            return source.Index == target.Index
                   && source.Type == target.Type
                   && string.Equals(source.Name, target.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetDeclarationKind(
            ScriptVariableInfo variable,
            string? sourceText,
            out ScriptVariableDeclarationKind kind)
        {
            if (variable.Type is not (0 or 1) || string.IsNullOrWhiteSpace(variable.Name))
            {
                kind = default;
                return false;
            }

            if (!ScriptVariableDeclarationParser.TryGetKind(sourceText, variable.Name, out kind))
            {
                return false;
            }

            return variable.Type == 1
                ? kind is ScriptVariableDeclarationKind.Short
                    or ScriptVariableDeclarationKind.Long
                    or ScriptVariableDeclarationKind.Int
                : kind is ScriptVariableDeclarationKind.Float or ScriptVariableDeclarationKind.Reference;
        }

        private static string? ReadSingleSourceText(ParsedMainRecord script)
        {
            var sources = script.Subrecords
                .Where(static subrecord => subrecord.Signature == "SCTX")
                .ToArray();
            return sources.Length == 1 ? sources[0].DataAsString : null;
        }
    }
}

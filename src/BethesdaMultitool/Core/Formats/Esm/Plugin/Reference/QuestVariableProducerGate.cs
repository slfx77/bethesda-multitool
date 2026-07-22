using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     One emitted record and exact embedded script path that structurally writes a recovered
///     quest-local channel. INFO owners are provisional until the dialogue writer records the
///     same source identity and script path in its actual-emission ledger.
/// </summary>
internal sealed record QuestVariableProducerOwner(
    string RecordType,
    uint SourceFormId,
    string ScriptPath);

/// <summary>Structurally proven source-local write made by one producer owner.</summary>
internal sealed record QuestVariableProducerEvidence(
    QuestVariableRecoveryMapping Mapping,
    QuestVariableProducerOwner Owner);

/// <summary>
///     Final-output obligation for one append-only local. At least one candidate owner must
///     appear in PlanWriter's or the dialogue writer's actual-emission ledger.
/// </summary>
internal sealed record QuestVariableProducerRequirement(
    ScriptVariableAugmentation Augmentation,
    ImmutableArray<QuestVariableProducerOwner> CandidateOwners);

/// <summary>
///     Exact non-dialogue script owners recorded by PlanWriter only after the enclosing
///     SCPT/PACK/TERM record and the named script block were serialized.
/// </summary>
internal sealed record PlanProducerEmissionLedger(
    ImmutableArray<QuestVariableProducerOwner> Owners)
{
    public static PlanProducerEmissionLedger Empty { get; } = new([]);

    public bool Contains(QuestVariableProducerOwner owner) => Owners.Contains(owner);
}

internal sealed record QuestVariableProducerGateDiagnostic(
    string Code,
    string RecordType,
    uint RecordFormId,
    string? EditorId,
    uint TargetQuestFormId,
    uint TargetScriptFormId,
    uint TargetVariableIndex,
    string? TargetVariableName,
    string Message,
    IReadOnlyDictionary<string, string?> Metadata);

internal sealed record QuestVariableProducerGateResult(
    int SuppressedInfoCount,
    int SuppressedPackageCount,
    int SuppressedTerminalMenuItemCount,
    ImmutableArray<ScriptVariableAugmentation> ScriptVariableAugmentations,
    ImmutableArray<QuestVariableRecoveryMapping> VariableRecoveryMappings,
    ImmutableArray<QuestVariableProducerRequirement> ProducerRequirements,
    IReadOnlyList<QuestVariableProducerGateDiagnostic> Diagnostics);

/// <summary>
///     Prevents an append-only quest local from making a recovered INFO/PACK appear usable
///     when no emitted script is proven to write it. Exact remaps to an existing retail local
///     bypass this gate because retail behavior remains the producer for that existing state.
/// </summary>
internal static class QuestVariableProducerGate
{
    public static ImmutableArray<QuestVariableRecoveryMapping> SelectFreshMappings(
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyList<ScriptVariableAugmentation> augmentations)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(augmentations);

        return mappings
            .Where(mapping => FindAugmentation(mapping, augmentations) is not null)
            .OrderBy(static mapping => mapping.SourceQuestFormId)
            .ThenBy(static mapping => mapping.SourceVariable.Index)
            .ThenBy(static mapping => mapping.TargetScriptFormId)
            .ThenBy(static mapping => mapping.TargetVariable.Index)
            .ToImmutableArray();
    }

    public static QuestVariableProducerGateResult Apply(
        RecordCollection records,
        IReadOnlyList<ScriptVariableAugmentation> augmentations,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyList<QuestVariableProducerEvidence> producerEvidence,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? formIdAliases)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(augmentations);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(producerEvidence);
        ArgumentNullException.ThrowIfNull(masterRecordsByFormId);

        if (augmentations.Count == 0)
        {
            return new QuestVariableProducerGateResult(
                0,
                0,
                0,
                [],
                [.. mappings],
                [],
                []);
        }

        var augmentationSet = augmentations.ToHashSet();
        var freshMappings = mappings
            .Where(mapping => FindAugmentation(mapping, augmentations) is not null)
            .ToArray();
        var exactMappings = mappings
            .Where(mapping => FindAugmentation(mapping, augmentations) is null)
            .ToArray();
        var targetIndex = BuildTargetIndex(freshMappings, augmentations, formIdAliases);
        var evidenceByChannel = producerEvidence
            .Select(evidence => new
            {
                Evidence = evidence,
                Augmentation = FindAugmentation(evidence.Mapping, augmentations)
            })
            .Where(static item => item.Augmentation is not null)
            .GroupBy(static item => item.Augmentation!)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static item => item.Evidence.Owner)
                    .Distinct()
                    .OrderBy(static owner => owner.RecordType, StringComparer.Ordinal)
                    .ThenBy(static owner => owner.SourceFormId)
                    .ThenBy(static owner => owner.ScriptPath, StringComparer.Ordinal)
                    .ToImmutableArray());

        var diagnostics = new List<QuestVariableProducerGateDiagnostic>();
        var suppressedInfos = 0;
        var suppressedPackages = 0;
        var suppressedTerminalMenuItems = 0;
        var terminalOwnerItems = BuildTerminalOwnerItemIndex(records, producerEvidence);

        // A PACK can itself be the sole producer for another dependent record. Removing a
        // PACK because one of its own conditions is unsupported can therefore invalidate a
        // second channel. Iterate to a fixed point instead of leaving that second dependency.
        while (true)
        {
            var liveOwners = BuildLiveOwnerSet(records, producerEvidence, terminalOwnerItems);
            var supportedChannels = augmentationSet
                .Where(augmentation => evidenceByChannel.TryGetValue(augmentation, out var owners)
                                       && owners.Any(liveOwners.Contains))
                .ToHashSet();
            var removedThisPass = 0;

            for (var index = records.Dialogues.Count - 1; index >= 0; index--)
            {
                var dialogue = records.Dialogues[index];
                if (IsMasterAnchored("INFO", dialogue.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                var unsupported = FindUnsupportedChannels(
                    dialogue.Conditions,
                    targetIndex,
                    supportedChannels,
                    formIdAliases);
                if (unsupported.Count == 0)
                {
                    continue;
                }

                records.Dialogues.RemoveAt(index);
                suppressedInfos++;
                removedThisPass++;
                AddDiagnostics(
                    diagnostics,
                    "INFO",
                    dialogue.FormId,
                    dialogue.EditorId,
                    unsupported,
                    freshMappings,
                    formIdAliases);
            }

            for (var index = records.Packages.Count - 1; index >= 0; index--)
            {
                var package = records.Packages[index];
                if (IsMasterAnchored("PACK", package.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                var unsupported = FindUnsupportedChannels(
                    package.Conditions,
                    targetIndex,
                    supportedChannels,
                    formIdAliases);
                if (unsupported.Count == 0)
                {
                    continue;
                }

                records.Packages.RemoveAt(index);
                suppressedPackages++;
                removedThisPass++;
                AddDiagnostics(
                    diagnostics,
                    "PACK",
                    package.FormId,
                    package.EditorId,
                    unsupported,
                    freshMappings,
                    formIdAliases);
            }

            for (var terminalIndex = records.Terminals.Count - 1; terminalIndex >= 0; terminalIndex--)
            {
                var terminal = records.Terminals[terminalIndex];
                if (IsMasterAnchored("TERM", terminal.FormId, masterRecordsByFormId, formIdAliases))
                {
                    continue;
                }

                List<TerminalMenuItem>? patchedItems = null;
                for (var itemIndex = terminal.MenuItems.Count - 1; itemIndex >= 0; itemIndex--)
                {
                    var item = terminal.MenuItems[itemIndex];
                    var unsupported = FindUnsupportedChannels(
                        item.Conditions,
                        targetIndex,
                        supportedChannels,
                        formIdAliases);
                    if (unsupported.Count == 0)
                    {
                        continue;
                    }

                    patchedItems ??= [.. terminal.MenuItems];
                    patchedItems.RemoveAt(itemIndex);
                    suppressedTerminalMenuItems++;
                    removedThisPass++;
                    AddDiagnostics(
                        diagnostics,
                        "TERM",
                        terminal.FormId,
                        terminal.EditorId,
                        unsupported,
                        freshMappings,
                        formIdAliases,
                        code: "quest-variable.menu-item-suppressed-no-emitted-producer",
                        subject: "terminal menu item",
                        extraMetadata: new Dictionary<string, string?>
                        {
                            ["terminal-menu-index"] = itemIndex.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            ["terminal-menu-text"] = item.Text,
                            ["terminal-result-text"] = item.ResultText,
                        });
                }

                if (patchedItems is not null)
                {
                    records.Terminals[terminalIndex] = terminal with { MenuItems = patchedItems };
                }
            }

            if (removedThisPass == 0)
            {
                break;
            }
        }

        var finalLiveOwners = BuildLiveOwnerSet(records, producerEvidence, terminalOwnerItems);
        var survivingAugmentations = augmentations
            .Where(augmentation => evidenceByChannel.TryGetValue(augmentation, out var owners)
                                   && owners.Any(finalLiveOwners.Contains))
            .OrderBy(static augmentation => augmentation.TargetScriptFormId)
            .ThenBy(static augmentation => augmentation.Variable.Index)
            .ThenBy(static augmentation => augmentation.Variable.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static augmentation => augmentation.Variable.Name, StringComparer.Ordinal)
            .ThenBy(static augmentation => augmentation.Variable.Type)
            .ToImmutableArray();
        var survivingMappings = exactMappings
            .Concat(freshMappings.Where(mapping =>
                FindAugmentation(mapping, survivingAugmentations) is not null))
            .OrderBy(static mapping => mapping.SourceQuestFormId)
            .ThenBy(static mapping => mapping.SourceVariable.Index)
            .ThenBy(static mapping => mapping.TargetQuestFormId)
            .ThenBy(static mapping => mapping.TargetVariable.Index)
            .ThenBy(static mapping => mapping.SourceVariable.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static mapping => mapping.SourceVariable.Name, StringComparer.Ordinal)
            .ThenBy(static mapping => mapping.SourceVariable.Type)
            .ToImmutableArray();
        var requirements = survivingAugmentations
            .Select(augmentation => new QuestVariableProducerRequirement(
                augmentation,
                evidenceByChannel[augmentation]
                    .Where(finalLiveOwners.Contains)
                    .Select(owner => NormalizeFinalOwner(owner, records, terminalOwnerItems))
                    .Distinct()
                    .OrderBy(static owner => owner.RecordType, StringComparer.Ordinal)
                    .ThenBy(static owner => owner.SourceFormId)
                    .ThenBy(static owner => owner.ScriptPath, StringComparer.Ordinal)
                    .ToImmutableArray()))
            .ToImmutableArray();

        diagnostics.Reverse();
        return new QuestVariableProducerGateResult(
            suppressedInfos,
            suppressedPackages,
            suppressedTerminalMenuItems,
            survivingAugmentations,
            survivingMappings,
            requirements,
            diagnostics);
    }

    /// <summary>
    ///     Plan-stage compatibility check after reference sanitation, drop cascades, and
    ///     record ordering have settled the immutable plan. Production also calls
    ///     <see cref="VerifyFinalOutput" /> after records are serialized.
    /// </summary>
    public static void VerifyFinalPlan(
        EmitPlan? plan,
        IReadOnlyList<QuestVariableProducerRequirement> requirements)
    {
        VerifyPlannedRequirements(plan, requirements);
    }

    /// <summary>
    ///     Planner-stage check. Requirements with an INFO candidate are deliberately deferred
    ///     until the separately-built dialogue byte stream supplies its actual-emission ledger.
    /// </summary>
    public static void VerifyPlannerState(
        EmitPlan? plan,
        IReadOnlyList<QuestVariableProducerRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        VerifyPlannedRequirements(
            plan,
            requirements.Where(static requirement =>
                requirement.CandidateOwners.All(static owner => owner.RecordType != "INFO")));
    }

    /// <summary>
    ///     Final check after dialogue combination, sanitation, and encoding. A provisional INFO
    ///     candidate satisfies an obligation only when that exact INFO/result-script path is in
    ///     the emitted ledger and its target-local write was structurally re-proven.
    /// </summary>
    public static void VerifyFinalOutput(
        IReadOnlyList<QuestVariableProducerRequirement> requirements,
        PlanProducerEmissionLedger planLedger,
        DialogueProducerLedger dialogueLedger)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(planLedger);
        ArgumentNullException.ThrowIfNull(dialogueLedger);
        VerifyEmittedRequirements(requirements, planLedger, dialogueLedger);
    }

    private static void VerifyPlannedRequirements(
        EmitPlan? plan,
        IEnumerable<QuestVariableProducerRequirement> requirements)
    {
        var materialized = requirements as IReadOnlyList<QuestVariableProducerRequirement>
                           ?? requirements.ToArray();
        if (materialized.Count == 0)
        {
            return;
        }

        if (plan is null)
        {
            throw new InvalidOperationException(
                "Append-only quest-local recovery requires a final EmitPlan so at least one " +
                "structurally proven producer can be verified after planner sanitation.");
        }

        foreach (var requirement in materialized)
        {
            var survivesPlannedOwner = requirement.CandidateOwners
                    .Where(static owner => owner.RecordType != "INFO")
                    .Any(owner => plan.Records.Any(record =>
                        record.Type == owner.RecordType
                        && record.SourceFormId == owner.SourceFormId
                        && record.Disposition is RecordDisposition.New or RecordDisposition.Override
                        && record.Model is not null));
            if (survivesPlannedOwner)
            {
                continue;
            }

            var candidates = string.Join(
                ", ",
                requirement.CandidateOwners.Select(owner =>
                    $"{owner.RecordType} 0x{owner.SourceFormId:X8} ({owner.ScriptPath})"));
            throw new InvalidOperationException(
                $"Append-only quest local '{requirement.Augmentation.Variable.Name}' " +
                $"(ID {requirement.Augmentation.Variable.Index}) on SCPT " +
                $"0x{requirement.Augmentation.TargetScriptFormId:X8} lost every proven producer " +
                $"from the final EmitPlan/dialogue emission ledger. Candidates: {candidates}.");
        }
    }

    private static void VerifyEmittedRequirements(
        IEnumerable<QuestVariableProducerRequirement> requirements,
        PlanProducerEmissionLedger planLedger,
        DialogueProducerLedger dialogueLedger)
    {
        foreach (var requirement in requirements)
        {
            var survivesPlannedOwner = requirement.CandidateOwners
                .Where(static owner => owner.RecordType != "INFO")
                .Any(planLedger.Contains);
            var survivesDialogueOwner = dialogueLedger.Evidence.Any(evidence =>
                Matches(evidence.Mapping, requirement.Augmentation)
                && requirement.CandidateOwners.Contains(evidence.Owner));
            if (survivesPlannedOwner || survivesDialogueOwner)
            {
                continue;
            }

            var candidates = string.Join(
                ", ",
                requirement.CandidateOwners.Select(owner =>
                    $"{owner.RecordType} 0x{owner.SourceFormId:X8} ({owner.ScriptPath})"));
            throw new InvalidOperationException(
                $"Append-only quest local '{requirement.Augmentation.Variable.Name}' " +
                $"(ID {requirement.Augmentation.Variable.Index}) on SCPT " +
                $"0x{requirement.Augmentation.TargetScriptFormId:X8} lost every proven producer " +
                "from the actual PlanWriter/dialogue emission ledgers. " +
                $"Candidates: {candidates}.");
        }
    }

    private static Dictionary<TargetConditionKey, ScriptVariableAugmentation> BuildTargetIndex(
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyList<ScriptVariableAugmentation> augmentations,
        IReadOnlyDictionary<uint, uint>? aliases)
    {
        var result = new Dictionary<TargetConditionKey, ScriptVariableAugmentation>();
        foreach (var mapping in mappings)
        {
            var key = new TargetConditionKey(
                ResolveAlias(mapping.TargetQuestFormId, aliases),
                mapping.TargetVariable.Index);
            var augmentation = FindAugmentation(mapping, augmentations)
                               ?? throw new InvalidOperationException(
                                   "Fresh quest-local mapping has no matching augmentation directive.");
            if (result.TryGetValue(key, out var existing) && existing != augmentation)
            {
                throw new InvalidDataException(
                    $"Ambiguous append-only quest-local target for quest 0x{key.QuestFormId:X8}, " +
                    $"variable ID {key.VariableIndex}.");
            }

            result[key] = augmentation;
        }

        return result;
    }

    private static HashSet<ScriptVariableAugmentation> FindUnsupportedChannels(
        IReadOnlyList<DialogueCondition> conditions,
        IReadOnlyDictionary<TargetConditionKey, ScriptVariableAugmentation> targetIndex,
        HashSet<ScriptVariableAugmentation> supportedChannels,
        IReadOnlyDictionary<uint, uint>? aliases)
    {
        var result = new HashSet<ScriptVariableAugmentation>();
        foreach (var condition in conditions)
        {
            if (condition.FunctionIndex != QuestVariableConditionSanitizer.GetQuestVariableFunctionIndex)
            {
                continue;
            }

            var key = new TargetConditionKey(
                ResolveAlias(condition.Parameter1, aliases),
                condition.Parameter2);
            if (targetIndex.TryGetValue(key, out var augmentation)
                && !supportedChannels.Contains(augmentation))
            {
                result.Add(augmentation);
            }
        }

        return result;
    }

    private static HashSet<QuestVariableProducerOwner> BuildLiveOwnerSet(
        RecordCollection records,
        IReadOnlyList<QuestVariableProducerEvidence> producerEvidence,
        Dictionary<QuestVariableProducerOwner, TerminalMenuItem> terminalOwnerItems)
    {
        var result = new HashSet<QuestVariableProducerOwner>();
        var liveScripts = records.Scripts.Select(static script => script.FormId).ToHashSet();
        var livePackages = records.Packages.Select(static package => package.FormId).ToHashSet();
        var liveTerminalItems = records.Terminals
            .SelectMany(static terminal => terminal.MenuItems)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        // INFO evidence has already passed the combined/deduplicated dialogue-plan gate.
        // Keep it provisional only while that exact source INFO still exists after the
        // producer gate's fixed-point suppression. Final survival is checked against the
        // actual dialogue emission ledger, not this model-presence set.
        var liveInfoSourceIds = records.Dialogues
            .Where(static info => info.FormId != 0)
            .Select(static info => info.FormId)
            .ToHashSet();
        foreach (var evidence in producerEvidence)
        {
            var owner = evidence.Owner;
            var isLive = owner.RecordType switch
            {
                "SCPT" => liveScripts.Contains(owner.SourceFormId),
                "PACK" => livePackages.Contains(owner.SourceFormId),
                "TERM" => terminalOwnerItems.TryGetValue(owner, out var item)
                          && liveTerminalItems.Contains(item),
                "INFO" => liveInfoSourceIds.Contains(owner.SourceFormId),
                _ => false,
            };
            if (isLive)
            {
                result.Add(owner);
            }
        }

        return result;
    }

    private static void AddDiagnostics(
        List<QuestVariableProducerGateDiagnostic> diagnostics,
        string recordType,
        uint recordFormId,
        string? editorId,
        IReadOnlySet<ScriptVariableAugmentation> unsupported,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, uint>? aliases,
        string code = "quest-variable.record-suppressed-no-emitted-producer",
        string subject = "entire new record",
        IReadOnlyDictionary<string, string?>? extraMetadata = null)
    {
        foreach (var augmentation in unsupported
                     .OrderBy(static value => value.TargetScriptFormId)
                     .ThenBy(static value => value.Variable.Index))
        {
            var mapping = mappings.First(candidate => Matches(candidate, augmentation));
            var metadata = new Dictionary<string, string?>
            {
                ["record-editor-id"] = editorId,
                ["target-quest-form-id"] = $"0x{ResolveAlias(mapping.TargetQuestFormId, aliases):X8}",
                ["target-script-form-id"] = $"0x{augmentation.TargetScriptFormId:X8}",
                ["target-variable-index"] = augmentation.Variable.Index.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["target-variable-name"] = augmentation.Variable.Name,
                ["producer-policy"] = "planned-owner-or-emitted-dialogue-bundle-write",
            };
            if (extraMetadata is not null)
            {
                foreach (var (key, value) in extraMetadata)
                {
                    metadata[key] = value;
                }
            }

            diagnostics.Add(new QuestVariableProducerGateDiagnostic(
                code,
                recordType,
                recordFormId,
                editorId,
                ResolveAlias(mapping.TargetQuestFormId, aliases),
                augmentation.TargetScriptFormId,
                augmentation.Variable.Index,
                augmentation.Variable.Name,
                $"Suppressed the {subject} because its append-only GetQuestVariable " +
                "local has no structurally proven writer in an emission-eligible SCPT, PACK, " +
                "TERM, or combined-dialogue INFO result script.",
                metadata));
        }
    }

    private static Dictionary<QuestVariableProducerOwner, TerminalMenuItem> BuildTerminalOwnerItemIndex(
        RecordCollection records,
        IReadOnlyList<QuestVariableProducerEvidence> producerEvidence)
    {
        var terminalOwners = producerEvidence
            .Where(static evidence => evidence.Owner.RecordType == "TERM")
            .Select(static evidence => evidence.Owner)
            .ToHashSet();
        var result = new Dictionary<QuestVariableProducerOwner, TerminalMenuItem>();
        foreach (var terminal in records.Terminals)
        {
            for (var itemIndex = 0; itemIndex < terminal.MenuItems.Count; itemIndex++)
            {
                var owner = new QuestVariableProducerOwner(
                    "TERM",
                    terminal.FormId,
                    BuildTerminalScriptPath(terminal, itemIndex));
                if (terminalOwners.Contains(owner))
                {
                    result[owner] = terminal.MenuItems[itemIndex];
                }
            }
        }

        return result;
    }

    private static QuestVariableProducerOwner NormalizeFinalOwner(
        QuestVariableProducerOwner owner,
        RecordCollection records,
        Dictionary<QuestVariableProducerOwner, TerminalMenuItem> terminalOwnerItems)
    {
        if (owner.RecordType != "TERM" || !terminalOwnerItems.TryGetValue(owner, out var item))
        {
            return owner;
        }

        var terminal = records.Terminals.Single(terminal => terminal.FormId == owner.SourceFormId);
        var currentIndex = terminal.MenuItems.FindIndex(candidate => ReferenceEquals(candidate, item));
        if (currentIndex < 0)
        {
            throw new InvalidOperationException(
                $"Live TERM producer 0x{owner.SourceFormId:X8} ({owner.ScriptPath}) lost its menu item.");
        }

        return owner with { ScriptPath = BuildTerminalScriptPath(terminal, currentIndex) };
    }

    private static string BuildTerminalScriptPath(
        TerminalRecord terminal,
        int itemIndex) =>
        $"{terminal.EditorId ?? $"TERM 0x{terminal.FormId:X8}"}/menu[{itemIndex}]";

    private static ScriptVariableAugmentation? FindAugmentation(
        QuestVariableRecoveryMapping mapping,
        IReadOnlyList<ScriptVariableAugmentation> augmentations) =>
        augmentations.FirstOrDefault(augmentation => Matches(mapping, augmentation));

    private static bool Matches(
        QuestVariableRecoveryMapping mapping,
        ScriptVariableAugmentation augmentation) =>
        mapping.TargetScriptFormId == augmentation.TargetScriptFormId
        && mapping.TargetVariable == augmentation.Variable
        && mapping.DeclarationKind == augmentation.DeclarationKind;

    private static bool IsMasterAnchored(
        string signature,
        uint sourceFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? aliases)
    {
        var resolved = ResolveAlias(sourceFormId, aliases);
        return masterRecordsByFormId.TryGetValue(resolved, out var master)
               && master.Header.Signature == signature;
    }

    private static uint ResolveAlias(uint formId, IReadOnlyDictionary<uint, uint>? aliases)
    {
        if (formId == 0 || aliases is null)
        {
            return formId;
        }

        var current = formId;
        var visited = new HashSet<uint>();
        while (visited.Add(current)
               && aliases.TryGetValue(current, out var mapped)
               && mapped != 0
               && mapped != current)
        {
            current = mapped;
        }

        return current;
    }

    private readonly record struct TargetConditionKey(uint QuestFormId, uint VariableIndex);

}

using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Proven quest-local writes carried by dialogue INFO result-script bundles. Candidate
///     evidence comes from the combined/deduplicated dialogue plan, never from the raw INFO
///     capture list. The emitted ledger is populated only after an INFO has passed every
///     dialogue gate and its complete inline script bundle has been encoded.
/// </summary>
internal sealed record DialogueProducerLedger(
    ImmutableArray<QuestVariableProducerEvidence> Evidence)
{
    public static DialogueProducerLedger Empty { get; } = new([]);

    public static DialogueProducerLedger FromCombinedOutput(
        DialogueCombinePlan combinePlan,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        IReadOnlyDictionary<uint, uint>? formIdAliases)
    {
        ArgumentNullException.ThrowIfNull(combinePlan);
        ArgumentNullException.ThrowIfNull(mappings);

        return new DialogueProducerLedger(
            QuestVariableBytecodeRemapper.FindInfoProducerWrites(
                combinePlan.NewInfos,
                mappings,
                formIdAliases,
                QuestVariableBytecodeRemapper.ProducerOperandIdentity.Source));
    }
}

/// <summary>
///     Records only INFO result scripts that reached the byte stream. The source INFO
///     identity remains stable across FormID allocation, while the scan uses target local
///     IDs because quest-variable bytecode remapping has already run by this phase.
/// </summary>
internal sealed class DialogueProducerLedgerBuilder
{
    private readonly List<QuestVariableProducerEvidence> _evidence = [];
    private readonly IReadOnlyDictionary<uint, uint>? _formIdAliases;
    private readonly IReadOnlyList<QuestVariableRecoveryMapping> _mappings;

    public DialogueProducerLedgerBuilder(
        IReadOnlyList<QuestVariableRecoveryMapping>? mappings,
        IReadOnlyDictionary<uint, uint>? formIdAliases)
    {
        _mappings = mappings ?? [];
        _formIdAliases = formIdAliases;
    }

    public void RecordEmittedInfo(
        DialogueRecord sourceInfo,
        DialogueRecord emittedInfo,
        IReadOnlySet<uint> validFormIds,
        IReadOnlyDictionary<uint, uint>? emissionRemapTable)
    {
        ArgumentNullException.ThrowIfNull(sourceInfo);
        ArgumentNullException.ThrowIfNull(emittedInfo);
        ArgumentNullException.ThrowIfNull(validFormIds);

        if (_mappings.Count == 0 || sourceInfo.FormId == 0)
        {
            return;
        }

        _evidence.AddRange(QuestVariableBytecodeRemapper.FindInfoProducerWrites(
            emittedInfo,
            sourceInfo.FormId,
            sourceInfo.EditorId,
            _mappings,
            _formIdAliases,
            QuestVariableBytecodeRemapper.ProducerOperandIdentity.Target,
            validFormIds,
            emissionRemapTable));
    }

    public DialogueProducerLedger Build()
    {
        return new DialogueProducerLedger(
            _evidence
                .Distinct()
                .OrderBy(static item => item.Mapping.TargetScriptFormId)
                .ThenBy(static item => item.Mapping.TargetVariable.Index)
                .ThenBy(static item => item.Owner.SourceFormId)
                .ThenBy(static item => item.Owner.ScriptPath, StringComparer.Ordinal)
                .ToImmutableArray());
    }
}

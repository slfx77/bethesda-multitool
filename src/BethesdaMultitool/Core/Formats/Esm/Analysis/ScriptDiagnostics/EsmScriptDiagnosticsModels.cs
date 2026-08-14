using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>Aggregated script/dialogue diagnostics gathered for a set of target records in an ESM/ESP.</summary>
public sealed record EsmScriptDiagnosticsResult(
    string SourcePath,
    IReadOnlyList<string> Targets,
    IReadOnlyList<EsmScriptDiagnosticTargetMatchRow> TargetMatches,
    IReadOnlyList<EsmScriptDiagnosticRecordRow> Records,
    IReadOnlyList<EsmScriptDiagnosticDialogueRow> Dialogue,
    IReadOnlyList<EsmScriptDialogueAuditRow> DialogueAudit,
    IReadOnlyList<EsmScriptConditionAuditRow> Conditions,
    IReadOnlyList<EsmScriptDiagnosticBlockRow> ScriptBlocks,
    IReadOnlyList<EsmScriptDiagnosticReferenceRow> ScriptReferences)
{
    /// <summary>
    ///     Game identity used for condition-layout and semantic decoding. Unknown preserves the
    ///     pre-game-aware constructor contract while making condition interpretation fail closed.
    /// </summary>
    public BethesdaGame Game { get; init; } = BethesdaGame.Unknown;
}

/// <summary>A record that matched one of the requested diagnostic targets, with the reason it matched.</summary>
public sealed record EsmScriptDiagnosticTargetMatchRow(
    string Target,
    string RecordType,
    uint FormId,
    string EditorId,
    string FullName,
    string MatchReason);

/// <summary>A record related to a target (and how), summarizing its interesting subrecords.</summary>
public sealed record EsmScriptDiagnosticRecordRow(
    string Target,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    string FullName,
    string InterestingSubrecords);

/// <summary>A dialogue INFO line associated with a target, with its topic/quest/speaker links and response info.</summary>
public sealed record EsmScriptDiagnosticDialogueRow(
    string Target,
    uint InfoFormId,
    string InfoEditorId,
    uint TopicFormId,
    string TopicLabel,
    uint QuestFormId,
    uint SpeakerFormId,
    uint PreviousInfo,
    string LinkToTopics,
    string LinkFromTopics,
    string AddTopics,
    string FollowUpInfos,
    string InfoFlags,
    int ResponseCount,
    bool HasResultScript,
    string ResponsePreview);

/// <summary>Audit row diagnosing whether a dialogue INFO is reachable (root/terminal/goodbye classification and topic edges).</summary>
public sealed record EsmScriptDialogueAuditRow(
    string Target,
    uint InfoFormId,
    uint TopicFormId,
    string TopicLabel,
    uint QuestFormId,
    uint SpeakerFormId,
    string RootClassification,
    bool HasIncomingTopicEdge,
    bool HasExplicitRootLink,
    bool IsTerminalReturnCandidate,
    bool HasGoodbyeForSpeakerQuest,
    string RawTcltBytes,
    string LinkToTopics,
    string FollowUpInfos,
    string ResponsePreview);

/// <summary>
///     One decoded CTDA condition on a target-related record. <c>ComparisonRawBits</c> preserves
///     the exact serialized comparison union; <c>ComparisonValue</c> is its float projection and is
///     numeric only when <c>UsesGlobalComparison</c> is false. Nullable tail fields distinguish an
///     absent physical word from a serialized zero. <c>SemanticReferenceLabel</c> is populated only
///     when the game-aware policy says present reference storage is a Reference FormID.
/// </summary>
public sealed record EsmScriptConditionAuditRow(
    string Target,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    int ConditionIndex,
    string FunctionName,
    ushort FunctionIndex,
    byte Type,
    float ComparisonValue,
    uint ComparisonRawBits,
    string ComparisonGlobalLabel,
    uint Parameter1,
    string Parameter1Label,
    uint Parameter2,
    string Parameter2Label,
    uint? RunOn,
    uint? ReferenceStorage,
    string SemanticReferenceLabel,
    string RawBytes,
    int? Parameter3,
    bool ReferenceStorageIsSemantic,
    uint? SemanticReferenceFormId,
    int BodyLength,
    string LayoutStatus)
{
    /// <summary>Whether the comparison union is tagged as a GLOB FormID by CTDA Type bit 0x04.</summary>
    public bool UsesGlobalComparison => (Type & 0x04) != 0;

    /// <summary>The numeric comparison, or null when the union contains a GLOB FormID.</summary>
    public float? NumericComparisonValue => UsesGlobalComparison ? null : ComparisonValue;

    /// <summary>The comparison GLOB FormID, including zero, or null for a numeric comparison.</summary>
    public uint? ComparisonGlobalFormId => UsesGlobalComparison ? ComparisonRawBits : null;

    /// <summary>Stable discriminator used by diagnostics exports.</summary>
    public string ComparisonKind => UsesGlobalComparison ? "global_form_id" : "numeric";
}

/// <summary>One compiled-script (SCDA) block on a target-related record, comparing SCHR-declared sizes against the walk.</summary>
public sealed record EsmScriptDiagnosticBlockRow(
    string Target,
    string Relation,
    string RecordType,
    uint FormId,
    string EditorId,
    int BlockIndex,
    string SubrecordOrder,
    string OrderStatus,
    int ScdaLength,
    uint? SchrCompiledSize,
    uint? SchrReferenceCount,
    int ActualReferenceSlots,
    bool CompiledSizeMatches,
    bool RefCountMatches,
    bool WalkedToEnd,
    bool HasDiagnostics,
    string Diagnostics,
    string SourceTextPreview);

/// <summary>One reference slot inside a compiled-script block, with its raw value and FormID-resolution status.</summary>
public sealed record EsmScriptDiagnosticReferenceRow(
    string Target,
    string ParentRecordType,
    uint ParentFormId,
    int BlockIndex,
    int SlotIndex,
    string ReferenceKind,
    uint RawValue,
    uint ResolvedFormId,
    string Status,
    string ResolvedRecordType,
    string ResolvedEditorId,
    string ResolvedFullName);

/// <summary>Indexed identity (signature + editor/full name) of a FormID, used to resolve diagnostic labels.</summary>
internal sealed record EsmScriptFormIdInfo(
    uint FormId,
    string RecordType,
    string EditorId,
    string FullName);

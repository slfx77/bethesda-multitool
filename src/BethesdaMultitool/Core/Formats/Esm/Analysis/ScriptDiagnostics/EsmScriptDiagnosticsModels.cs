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
    IReadOnlyList<EsmScriptDiagnosticReferenceRow> ScriptReferences);

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

/// <summary>One decoded CTDA condition on a target-related record (function, operands, run-on, raw bytes).</summary>
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
    uint Parameter1,
    string Parameter1Label,
    uint Parameter2,
    string Parameter2Label,
    uint RunOn,
    uint Reference,
    string ReferenceLabel,
    string RawBytes);

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

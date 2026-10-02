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

    /// <summary>
    ///     Every explicitly requested record FormID (<c>--record</c>), sorted, found or not. Empty
    ///     when none was requested. <see cref="Targets" /> is empty in an explicit-only run.
    /// </summary>
    public IReadOnlyList<uint> ExplicitRecordFormIds { get; init; } = [];

    /// <summary>The explicitly requested FormIDs that no record in the file carries, sorted.</summary>
    public IReadOnlyList<uint> MissingExplicitRecordFormIds { get; init; } = [];

    /// <summary>
    ///     One row per menu item of every target-related TERM record (<c>target_terminal_items.csv</c>),
    ///     joining the item to its record-level CTDA ordinals (<see cref="Conditions" />) and its script
    ///     block indexes (<see cref="ScriptBlocks" />). Empty when no TERM record is related.
    /// </summary>
    public IReadOnlyList<EsmScriptTerminalItemRow> TerminalItems { get; init; } = [];

    /// <summary>Every selected physical QUST script occurrence, independent of the legacy FormID projection.</summary>
    public IReadOnlyList<EsmQuestScriptFragment> QuestScripts { get; init; } = [];

    /// <summary>SHA-256 of the exact bytes parsed by AnalyzeFile; unavailable for caller-provided record lists.</summary>
    public string? SourceSha256 { get; init; }

    /// <summary>Length of the exact source buffer parsed by AnalyzeFile.</summary>
    public long? SourceLength { get; init; }
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

/// <summary>
///     Audit row diagnosing whether a dialogue INFO is reachable (root/terminal/goodbye classification and topic
///     edges).
/// </summary>
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

/// <summary>
///     One compiled-script (SCDA) block on a target-related record, comparing SCHR-declared sizes against the walk.
///     <para>
///         <c>SourceTextPreview</c> is the first 180 characters of the block's SCTX (kept for existing readers);
///         <c>SourceText</c> is the whole SCTX, untruncated, with its line endings as stored. For a TERM record,
///         <c>OwnerMenuItemIndex</c>/<c>OwnerMenuItemText</c> name the menu item (1-based on-disk order, and its
///         ITXT) whose embedded script this block is; they are null/empty for any other record.
///     </para>
///     <para>
///         <see cref="SourceTextBytes" /> is the same SCTX as the exact bytes the plugin stores, which is what the
///         report's per-block source file holds; <c>SourceText</c> is those bytes decoded as Windows-1252 with every
///         trailing NUL trimmed.
///     </para>
/// </summary>
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
    string SourceTextPreview,
    string SourceText,
    int? OwnerMenuItemIndex,
    string OwnerMenuItemText)
{
    /// <summary>
    ///     The payload of the block's first SCTX subrecord exactly as stored, minus one trailing NUL when it carries
    ///     one: no decoding, no line-ending change. Null when the block has no SCTX subrecord; empty when its SCTX
    ///     holds nothing (or only that NUL). SCTX is a byte string, so this is the same in a big-endian container.
    /// </summary>
    public byte[]? SourceTextBytes { get; init; }
}

/// <summary>
///     One TERM menu item on a target-related terminal: the item's own subrecords (ITXT, RNAM, ANAM, INAM, TNAM)
///     joined to its CTDA conditions and its embedded script block(s).
///     <para>
///         <c>ItemIndex</c> is the 1-based on-disk position of the item's ITXT in the record (the engine's menu
///         order). <c>ConditionIndexes</c> are the record-level CTDA ordinals, the same numbers as
///         <c>condition_index</c> in <c>target_conditions.csv</c>, and <c>ConditionExpressions</c> holds the
///         shared condition describer's line for each, in the same order. A CTDA whose layout is not valid for
///         the game is listed raw and is not interpreted. <c>ScriptBlockIndexes</c> are the
///         <c>block_index</c> values of the item's rows in <c>target_result_scripts.csv</c>.
///     </para>
///     <para>
///         <c>FlagsRaw</c> is the ANAM byte. <c>FlagNames</c> names it only for Fallout 3 and New Vegas, whose
///         ANAM is a flags byte (bit 0 Add Note, bit 1 Force Redraw); it is empty for any other game (Fallout 4
///         stores a menu-item type enum in ANAM) and when ANAM is absent.
///     </para>
/// </summary>
public sealed record EsmScriptTerminalItemRow(
    string Target,
    string Relation,
    uint FormId,
    string EditorId,
    int ItemIndex,
    string ItemText,
    string? ResultText,
    byte? FlagsRaw,
    string FlagNames,
    uint? DisplayNoteFormId,
    string DisplayNoteLabel,
    uint? SubTerminalFormId,
    string SubTerminalLabel,
    IReadOnlyList<int> ConditionIndexes,
    IReadOnlyList<string> ConditionExpressions,
    IReadOnlyList<int> ScriptBlockIndexes,
    int? SourceTextLength);

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

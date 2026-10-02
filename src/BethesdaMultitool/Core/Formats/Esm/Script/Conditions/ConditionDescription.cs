using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     A structured, plain-text description of one CTDA condition in its list, built only by
///     <see cref="ConditionDescriber" />. Every string is plain text with no Spectre markup (callers escape).
///     <para>
///         The raw type byte is decoded as: operator <c>(Type &gt;&gt; 5) &amp; 7</c>, OR <c>Type &amp; 0x01</c>,
///         global comparison <c>Type &amp; 0x04</c>, swap subject/target <c>Type &amp; 0x10</c>.
///         <see cref="ConnectorToNext" /> and <see cref="OrGroup" /> report how the stored flags join the list;
///         the grouping is the GECK convention and is not asserted as engine semantics.
///     </para>
/// </summary>
public sealed record ConditionDescription
{
    /// <summary>Comparison kind: a numeric float.</summary>
    public const string ComparisonKindNumeric = "numeric";

    /// <summary>Comparison kind: the comparison union holds a GLOB FormID.</summary>
    public const string ComparisonKindGlobal = "global";

    /// <summary>1-based position in the source list (on-disk order).</summary>
    public required int Index { get; init; }

    /// <summary>The condition as parsed.</summary>
    public required DialogueCondition Raw { get; init; }

    /// <summary>The game whose condition table named the function.</summary>
    public required BethesdaGame Game { get; init; }

    /// <summary>The raw CTDA function index.</summary>
    public ushort FunctionIndex => Raw.FunctionIndex;

    /// <summary>The function name, or <c>Func 0xNNNN</c> when the game's table does not know the index.</summary>
    public required string FunctionName { get; init; }

    /// <summary>Whether the game's condition table knows the function.</summary>
    public required bool FunctionKnown { get; init; }

    /// <summary>The raw operator code, <c>(Type &gt;&gt; 5) &amp; 7</c>.</summary>
    public int OperatorCode => (Raw.Type >> 5) & 0x7;

    /// <summary>The operator (<c>==</c>, <c>!=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>), or <c>?(N)</c> for the undefined codes 6 and 7.</summary>
    public required string Operator { get; init; }

    /// <summary><see cref="ComparisonKindNumeric" /> or <see cref="ComparisonKindGlobal" />.</summary>
    public required string ComparisonKind { get; init; }

    /// <summary>The numeric comparison value; null for a global comparison or a non-finite float.</summary>
    public float? ComparisonValue { get; init; }

    /// <summary>The exact bits of the four-byte comparison union.</summary>
    public uint ComparisonRawBits => BitConverter.SingleToUInt32Bits(Raw.ComparisonValue);

    /// <summary>The GLOB FormID, for a global comparison.</summary>
    public uint? ComparisonGlobalFormId { get; init; }

    /// <summary>The GLOB's EditorID when known.</summary>
    public string? ComparisonGlobalEditorId { get; init; }

    /// <summary>The comparison as printed after the operator.</summary>
    public required string ComparisonDisplay { get; init; }

    /// <summary>Parameter slot 1, or null when the function does not declare it and its raw value is zero.</summary>
    public ConditionOperand? Parameter1 { get; init; }

    /// <summary>Parameter slot 2, or null when the function does not declare it and its raw value is zero.</summary>
    public ConditionOperand? Parameter2 { get; init; }

    /// <summary>The raw CTDA offset-20 word (Run On, or the FNV animation-body selector).</summary>
    public uint RunOnRaw => Raw.RunOn;

    /// <summary>
    ///     The Run On target name. A stored default is reported as <c>Subject</c>, the same default the
    ///     dialogue viewer leaves implicit.
    /// </summary>
    public required string RunOn { get; init; }

    /// <summary>Whether the Run On differs from the implicit default (the dialogue viewer shows it only then).</summary>
    public required bool RunOnExplicit { get; init; }

    /// <summary>The raw CTDA offset-24 storage, whether or not it is semantically a Reference.</summary>
    public uint ReferenceRaw => Raw.Reference;

    /// <summary>The Reference FormID when offset 24 is a semantic Reference for this game and function.</summary>
    public uint? Reference { get; init; }

    /// <summary>The Reference's EditorID when known.</summary>
    public string? ReferenceEditorId { get; init; }

    /// <summary>The Reference as <c>EDID [0xFORMID]</c>, or null when there is no semantic Reference.</summary>
    public string? ReferenceDisplay { get; init; }

    /// <summary>The signed trailing Parameter #3, when the layout carries one.</summary>
    public int? Parameter3 => Raw.Parameter3;

    /// <summary>The Parameter #3 label (e.g. <c>Quest Alias: 3</c>), or null when it is absent or the default.</summary>
    public string? Parameter3Label { get; init; }

    /// <summary>Whether the OR flag (<c>Type &amp; 0x01</c>) is set.</summary>
    public bool IsOr => Raw.IsOr;

    /// <summary>
    ///     True when this is the last condition and it still carries the OR flag, which joins it to nothing.
    ///     Reported, never hidden.
    /// </summary>
    public required bool OrFlagOnLast { get; init; }

    /// <summary><c>AND</c> or <c>OR</c> joining this condition to the next; null for the last condition.</summary>
    public string? ConnectorToNext { get; init; }

    /// <summary>
    ///     1-based group number: a maximal run of conditions joined by the OR flag shares a group, and
    ///     consecutive groups are joined by AND (GECK convention, not verified against the engine).
    /// </summary>
    public required int OrGroup { get; init; }

    /// <summary>Whether the swap subject/target flag (<c>Type &amp; 0x10</c>) is set.</summary>
    public bool SwapSubjectTarget => ConditionTypeFlags.HasModernFlags(Game) && Raw.IsSubjectTargetSwapped;

    public bool SupportsModernTypeFlags => ConditionTypeFlags.HasModernFlags(Game);

    public bool UsePackData => SupportsModernTypeFlags && (Raw.Type & 0x08) != 0;

    public byte UnknownTypeBits => ConditionTypeFlags.UnknownBits(Raw.Type, Game);

    /// <summary>The raw CTDA type byte.</summary>
    public byte TypeRaw => Raw.Type;

    /// <summary>
    ///     The call and comparison, e.g.
    ///     <c>GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2</c>.
    /// </summary>
    public required string Expression { get; init; }
}

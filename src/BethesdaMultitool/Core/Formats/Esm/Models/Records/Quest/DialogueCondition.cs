namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     Parsed CTDA condition shared by dialogue, quests, packages, terminals, recipes, and effects.
/// </summary>
public record DialogueCondition
{
    /// <summary>Raw CTDA type byte (comparison/operator flags).</summary>
    public byte Type { get; init; }

    /// <summary>
    ///     Raw four-byte CTDA comparison union. This is the numeric comparison float when
    ///     <see cref="UsesGlobalComparison" /> is false; otherwise its exact bits are the comparison
    ///     GLOB FormID exposed by <see cref="ComparisonGlobalFormId" />.
    /// </summary>
    public float ComparisonValue { get; init; }

    /// <summary>Condition function index.</summary>
    public ushort FunctionIndex { get; init; }

    /// <summary>First function parameter (often a FormID).</summary>
    public uint Parameter1 { get; init; }

    /// <summary>Second function parameter.</summary>
    public uint Parameter2 { get; init; }

    /// <summary>
    ///     Raw CTDA offset-20 selector. It is normally Run On (Subject, Target, Reference, etc.);
    ///     two FNV functions instead interpret the same word as an animation-body selector.
    /// </summary>
    public uint RunOn { get; init; }

    /// <summary>
    ///     Raw CTDA offset-24 storage. Whether this is a Reference FormID is game- and
    ///     function-dependent; consumers must use <c>DialogueConditionReferencePolicy</c>.
    ///     Ignored storage is preserved for faithful round-trip and forensic display.
    /// </summary>
    public uint Reference { get; init; }

    /// <summary>
    ///     Signed trailing Parameter #3 from a complete 32-byte modern CTDA. Null means that the
    ///     source layout ended before bytes 28..31 (or that a runtime-only reconstruction did not
    ///     expose them); -1 is an authored/default value and is therefore distinct from absence.
    /// </summary>
    public int? Parameter3 { get; init; }

    /// <summary>
    ///     Value of an observed CIS1 sibling. When present, including an empty string, it is the
    ///     authoritative value for parameter slot 1 and <see cref="Parameter1" /> is placeholder storage.
    ///     Null means that no CIS1 sibling was observed.
    /// </summary>
    public string? Parameter1String { get; init; }

    /// <summary>
    ///     Value of an observed CIS2 sibling. When present, including an empty string, it is the
    ///     authoritative value for parameter slot 2 and <see cref="Parameter2" /> is placeholder storage.
    ///     Null means that no CIS2 sibling was observed.
    /// </summary>
    public string? Parameter2String { get; init; }

    /// <summary>Whether this condition is combined with the following condition using OR.</summary>
    public bool IsOr => (Type & 0x01) != 0;

    /// <summary>Whether the comparison union contains a GLOB FormID instead of a numeric float.</summary>
    public bool UsesGlobalComparison => (Type & 0x04) != 0;

    /// <summary>
    ///     GLOB FormID stored in the raw comparison union, or zero when this is a numeric comparison.
    /// </summary>
    public uint ComparisonGlobalFormId => UsesGlobalComparison
        ? BitConverter.SingleToUInt32Bits(ComparisonValue)
        : 0u;

    /// <summary>Whether subject and target are swapped.</summary>
    public bool IsSubjectTargetSwapped => (Type & 0x10) != 0;

    /// <summary>Human-readable comparison operator.</summary>
    public string ComparisonOperator => ((Type >> 5) & 0x7) switch
    {
        0 => "==",
        1 => "!=",
        2 => ">",
        3 => ">=",
        4 => "<",
        5 => "<=",
        _ => "?"
    };

    /// <summary>
    ///     Common legacy 0..4 Run-On name. Game/function-aware presenters must use
    ///     <c>DialogueConditionRunOnPolicy</c> for modern targets and the FNV body-selector exceptions.
    /// </summary>
    public string RunOnName => RunOn switch
    {
        0 => "Subject",
        1 => "Target",
        2 => "Reference",
        3 => "Combat Target",
        4 => "Linked Reference",
        _ => $"Unknown ({RunOn})"
    };
}

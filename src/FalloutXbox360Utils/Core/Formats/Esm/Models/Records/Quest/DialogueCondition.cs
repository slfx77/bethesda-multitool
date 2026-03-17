namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Parsed INFO CTDA condition.
/// </summary>
public record DialogueCondition
{
    /// <summary>Raw CTDA type byte (comparison/operator flags).</summary>
    public byte Type { get; init; }

    /// <summary>CTDA comparison value.</summary>
    public float ComparisonValue { get; init; }

    /// <summary>Condition function index.</summary>
    public ushort FunctionIndex { get; init; }

    /// <summary>First function parameter (often a FormID).</summary>
    public uint Parameter1 { get; init; }

    /// <summary>Second function parameter.</summary>
    public uint Parameter2 { get; init; }

    /// <summary>Run-on selector: Subject, Target, Reference, etc.</summary>
    public uint RunOn { get; init; }

    /// <summary>Reference FormID for RunOn=Reference/LinkedRef conditions.</summary>
    public uint Reference { get; init; }

    /// <summary>Whether this condition is ORed with the previous one.</summary>
    public bool IsOr => (Type & 0x01) != 0;

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

    /// <summary>Human-readable run-on target.</summary>
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

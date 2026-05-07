namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     A parsed condition from a perk's TESCondition linked list.
///     Captures skill/stat requirements (GetActorValue) and perk prerequisites (HasPerk).
/// </summary>
public record PerkCondition
{
    /// <summary>Condition function index (e.g., 0x0E = GetActorValue, 0x1C1 = HasPerk).</summary>
    public ushort FunctionIndex { get; init; }

    /// <summary>Human-readable function name.</summary>
    public string FunctionName { get; init; } = "";

    /// <summary>First parameter (ActorValue enum index for GetActorValue, FormID for HasPerk).</summary>
    public uint Parameter1 { get; init; }

    /// <summary>Resolved display for Parameter1 (skill name or perk name).</summary>
    public string? Parameter1Display { get; init; }

    /// <summary>FormID reference for Parameter1, if applicable (HasPerk target).</summary>
    public uint? Parameter1FormId { get; init; }

    /// <summary>Second parameter from the CTDA payload.</summary>
    public uint Parameter2 { get; init; }

    /// <summary>Resolved display for Parameter2, if applicable.</summary>
    public string? Parameter2Display { get; init; }

    /// <summary>FormID reference for Parameter2, if applicable.</summary>
    public uint? Parameter2FormId { get; init; }

    /// <summary>Comparison operator (0==, 1!=, 2>, 3>=, 4&lt;, 5&lt;=).</summary>
    public byte ComparisonOperator { get; init; }

    /// <summary>Comparison value (e.g., skill level threshold).</summary>
    public float ComparisonValue { get; init; }

    /// <summary>Human-readable comparison operator.</summary>
    public string OperatorDisplay => ComparisonOperator switch
    {
        0 => "==",
        1 => "!=",
        2 => ">",
        3 => ">=",
        4 => "<",
        5 => "<=",
        _ => $"?{ComparisonOperator}"
    };
}

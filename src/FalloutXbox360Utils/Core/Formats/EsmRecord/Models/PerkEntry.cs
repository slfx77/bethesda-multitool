namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Perk entry data from PRKE/PRKC/EPFT chains.
/// </summary>
public record PerkEntry
{
    /// <summary>Entry type (0=Quest Stage, 1=Ability, 2=Entry Point).</summary>
    public byte Type { get; init; }

    /// <summary>Rank for this entry.</summary>
    public byte Rank { get; init; }

    /// <summary>Priority within rank.</summary>
    public byte Priority { get; init; }

    /// <summary>Associated ability FormID (for type 1).</summary>
    public uint? AbilityFormId { get; init; }

    /// <summary>Human-readable entry type name.</summary>
    public string TypeName => Type switch
    {
        0 => "Quest Stage",
        1 => "Ability",
        2 => "Entry Point",
        _ => $"Unknown ({Type})"
    };
}

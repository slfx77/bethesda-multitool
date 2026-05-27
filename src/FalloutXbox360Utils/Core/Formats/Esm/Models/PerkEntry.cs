namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

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

    /// <summary>Associated quest FormID (for quest-stage entries).</summary>
    public uint? QuestFormId { get; init; }

    /// <summary>Quest stage for quest-stage entries.</summary>
    public int? QuestStage { get; init; }

    /// <summary>Entry point identifier for entry-point entries.</summary>
    public byte? EntryPoint { get; init; }

    /// <summary>Entry point function type from EPFT.</summary>
    public byte? FunctionType { get; init; }

    /// <summary>Function data value from EPFD, when it is a float payload.</summary>
    public float? EffectValue { get; init; }

    /// <summary>Function data FormID from EPFD/DATA, when it is a form reference payload.</summary>
    public uint? EffectFormId { get; init; }

    /// <summary>Raw/decoded entry data summary for payloads that are not fully typed yet.</summary>
    public string? EffectData { get; init; }

    /// <summary>Raw DATA payload for entry types whose layout is not fully modeled.</summary>
    public byte[]? RawEntryData { get; init; }

    /// <summary>Raw EPFD payload for function types whose layout is not fully modeled.</summary>
    public byte[]? RawFunctionData { get; init; }

    /// <summary>Condition-tab count from PRKC, when present.</summary>
    public byte? ConditionTabCount { get; init; }

    /// <summary>Conditions scoped to this perk entry.</summary>
    public List<PerkCondition> Conditions { get; init; } = [];

    /// <summary>Human-readable entry type name.</summary>
    public string TypeName => Type switch
    {
        0 => "Quest Stage",
        1 => "Ability",
        2 => "Entry Point",
        _ => $"Unknown ({Type})"
    };

    /// <summary>Human-readable function type name.</summary>
    public string? FunctionTypeName => FunctionType switch
    {
        null => null,
        0 => "Set Value",
        1 => "Add Value",
        2 => "Multiply Value",
        3 => "Add Range To Value",
        4 => "Add Actor Value Mult",
        5 => "Absolute Value",
        6 => "Negative Absolute Value",
        7 => "Add Leveled List",
        8 => "Add Activate Choice",
        _ => $"Unknown ({FunctionType.Value})"
    };
}

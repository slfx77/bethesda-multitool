using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

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

    /// <summary>Entry-point result function stored in DATA byte 1.</summary>
    public byte? EntryPointFunction { get; init; }

    /// <summary>Number of entry-point condition tabs stored in DATA byte 2.</summary>
    public byte? PerkConditionTabCount { get; init; }

    /// <summary>Function parameter-data kind from EPFT, distinct from EntryPointFunction.</summary>
    public byte? FunctionType { get; init; }

    /// <summary>Function data value from EPFD, when it is a float payload.</summary>
    public float? EffectValue { get; init; }

    /// <summary>Second numeric parameter for the TwoValue function-data class.</summary>
    public float? EffectValue2 { get; init; }

    public string? ActivationLabel { get; init; }
    public ushort? ActivationFlags { get; init; }
    public DialogueResultScript? ActivationScript { get; init; }

    /// <summary>Captured runtime structures; these bytes are not serialized DATA/EPFD payloads.</summary>
    public uint? RuntimeAddress { get; init; }
    public string? RuntimeClassName { get; init; }
    public string? RuntimeLayoutBasis { get; init; }
    public string? RuntimeLayoutStatus { get; init; }
    public byte[]? RuntimeRawData { get; init; }
    public uint? RuntimeFunctionAddress { get; init; }
    public string? RuntimeFunctionClassName { get; init; }
    public byte[]? RuntimeFunctionData { get; init; }
    public List<string> RecoveryIssues { get; init; } = [];

    /// <summary>Function data FormID from EPFD/DATA, when it is a form reference payload.</summary>
    public uint? EffectFormId { get; init; }

    /// <summary>Raw/decoded entry data summary for payloads that are not fully typed yet.</summary>
    public string? EffectData { get; init; }

    /// <summary>Raw DATA payload for entry types whose layout is not fully modeled.</summary>
    public byte[]? RawEntryData { get; init; }

    /// <summary>Raw EPFD payload for function types whose layout is not fully modeled.</summary>
    public byte[]? RawFunctionData { get; init; }

    /// <summary>Ordered PRKC + CTDA condition groups scoped to this perk entry.</summary>
    public List<PerkConditionGroup> ConditionGroups { get; init; } = [];

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
        0 => "None",
        1 => "One Value",
        2 => "Two Values",
        3 => "Leveled List",
        4 => "Activate Choice",
        _ => $"Unknown ({FunctionType.Value})"
    };
}

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Quest objective information from QOBJ + NNAM subrecords.
/// </summary>
public record QuestObjective
{
    /// <summary>Objective index value.</summary>
    public int Index { get; init; }

    /// <summary>Objective display text (NNAM subrecord).</summary>
    public string? DisplayText { get; init; }

    /// <summary>Target stage for completion.</summary>
    public int? TargetStage { get; init; }
}

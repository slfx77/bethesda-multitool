using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;

/// <summary>
///     Quest stage information from INDX + CNAM/QSDT subrecords.
/// </summary>
public record QuestStage
{
    /// <summary>Stage index value.</summary>
    public int Index { get; init; }

    /// <summary>Log entry text (CNAM subrecord, null-terminated).</summary>
    public string? LogEntry { get; init; }

    /// <summary>Stage flags (from QSDT).</summary>
    public byte Flags { get; init; }

    /// <summary>Per-stage conditions (CTDA* between QSDT and CNAM, with optional CIS1/CIS2).</summary>
    public List<DialogueCondition> Conditions { get; init; } = [];
}

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

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
}

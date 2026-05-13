namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Hardcore-mode survival stage record. Shared shape for RADS (radiation),
///     DEHY (dehydration), HUNG (hunger), SLPD (sleep deprivation). Each is a
///     48-byte PDB struct with an 8-byte DATA tuple holding (threshold, actor
///     value modifier). One record per stage (typically 5 stages per category).
/// </summary>
public record SurvivalStageRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Threshold value at which the stage triggers (DATA byte 0-3).</summary>
    public uint Threshold { get; init; }

    /// <summary>Actor value modifier applied at the stage (DATA byte 4-7).</summary>
    public uint Modifier { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Reconstructed Reputation (REPU) from memory dump.
///     FNV-specific faction reputation threshold definitions.
/// </summary>
public record ReputationRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Positive reputation value from DATA.</summary>
    public float PositiveValue { get; init; }

    /// <summary>Negative reputation value from DATA.</summary>
    public float NegativeValue { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

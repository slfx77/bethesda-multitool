namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight item snapshot for version tracking.
///     Covers ALCH (consumables), MISC, and KEYM records.
/// </summary>
public record TrackedItem
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>ESM record type: "ALCH", "MISC", or "KEYM".</summary>
    public required string RecordType { get; init; }

    public int Value { get; init; }
    public float Weight { get; init; }

    /// <summary>ALCH flags (for consumables only).</summary>
    public uint Flags { get; init; }

    /// <summary>Effect FormIDs (for ALCH consumables only).</summary>
    public List<uint> EffectFormIds { get; init; } = [];
}

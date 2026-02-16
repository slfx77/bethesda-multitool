namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight armor snapshot for version tracking.
/// </summary>
public record TrackedArmor
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public int Value { get; init; }
    public float Weight { get; init; }
    public float DamageThreshold { get; init; }
    public int DamageResistance { get; init; }
    public uint BipedFlags { get; init; }
}

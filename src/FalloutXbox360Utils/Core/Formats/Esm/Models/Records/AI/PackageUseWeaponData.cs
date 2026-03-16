namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Package Use Weapon data from PKW3.
/// </summary>
public record PackageUseWeaponData
{
    public bool AlwaysHit { get; init; }
    public bool DoNoDamage { get; init; }
    public bool Crouch { get; init; }
    public bool HoldFire { get; init; }
    public bool VolleyFire { get; init; }
    public bool RepeatFire { get; init; }
    public ushort BurstCount { get; init; }
    public ushort VolleyShotsMin { get; init; }
    public ushort VolleyShotsMax { get; init; }
    public float VolleyWaitMin { get; init; }
    public float VolleyWaitMax { get; init; }
    public uint? WeaponFormId { get; init; }
}

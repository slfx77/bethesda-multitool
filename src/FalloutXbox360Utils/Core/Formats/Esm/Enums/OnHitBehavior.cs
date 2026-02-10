namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     On-hit behavior from DNAM (HitBehavior uint32).
///     Determines dismemberment/explosion behavior on kill.
/// </summary>
public enum OnHitBehavior : uint
{
    Normal = 0,
    DismemberOnly = 1,
    ExplodeOnly = 2,
    NoDismemberOrExplode = 3
}

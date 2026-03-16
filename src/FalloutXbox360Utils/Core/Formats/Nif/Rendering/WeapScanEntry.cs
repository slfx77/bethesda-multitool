using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanned WEAP record data used for static best-weapon resolution.
/// </summary>
internal sealed class WeapScanEntry
{
    public string? EditorId { get; init; }
    public string? ModelPath { get; init; }
    public string? Mod2ModelPath { get; init; }
    public WeaponType WeaponType { get; init; }
    public short Damage { get; init; }
    public int Health { get; init; }
    public float ShotsPerSec { get; init; }
    public float Spread { get; init; }
    public float MinRange { get; init; }
    public float MaxRange { get; init; }
    public byte Flags { get; init; }
    public uint FlagsEx { get; init; }
    public uint? AmmoFormId { get; init; }
    public uint SkillActorValue { get; init; }
    public uint SkillRequirement { get; init; }
    public uint StrengthRequirement { get; init; }
    public byte HandGripAnim { get; init; }
    public string? EmbeddedWeaponNode { get; init; }
}

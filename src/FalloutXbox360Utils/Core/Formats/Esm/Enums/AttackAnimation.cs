namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     Attack animation type from DNAM byte 34.
/// </summary>
public enum AttackAnimation : byte
{
    AttackLeft = 26,
    AttackRight = 32,
    Attack3 = 38,
    Attack4 = 44,
    Attack5 = 50,
    Attack6 = 56,
    Attack7 = 62,
    Attack8 = 68,
    AttackLoop = 74,
    AttackSpin = 80,
    AttackSpin2 = 86,
    PlaceMine = 97,
    PlaceMine2 = 100,
    AttackThrow = 103,
    AttackThrow2 = 106,
    AttackThrow3 = 109,
    AttackThrow4 = 112,
    AttackThrow5 = 115,
    Default = 255
}

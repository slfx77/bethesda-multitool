namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     Hand grip animation type from DNAM byte 17.
///     Determines how the weapon is gripped by the character.
/// </summary>
public enum HandGripAnimation : byte
{
    HandGrip1 = 171,
    HandGrip2 = 172,
    HandGrip3 = 173,
    Default = 255
}

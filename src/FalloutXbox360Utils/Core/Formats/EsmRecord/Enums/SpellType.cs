namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;

/// <summary>
///     Spell type classification from SPEL SPIT subrecord.
/// </summary>
public enum SpellType : uint
{
    Spell = 0,
    Disease = 1,
    Power = 2,
    LesserPower = 3,
    Ability = 4,
    Poison = 5,
    Addiction = 10
}

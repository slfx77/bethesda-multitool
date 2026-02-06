namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;

/// <summary>
///     ACBS subrecord - Actor Base Stats.
///     24 bytes in NPC_/CREA records.
/// </summary>
public record ActorBaseSubrecord(
    uint Flags,
    ushort FatigueBase,
    ushort BarterGold,
    short Level,
    ushort CalcMin,
    ushort CalcMax,
    ushort SpeedMultiplier,
    float KarmaAlignment,
    short DispositionBase,
    ushort TemplateFlags,
    long Offset,
    bool IsBigEndian);

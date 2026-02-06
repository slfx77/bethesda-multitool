namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;

/// <summary>
///     CTDA subrecord - Condition data.
///     Used in quests, dialogues, and packages.
/// </summary>
public record ConditionSubrecord(
    byte Type,
    byte Operator,
    float ComparisonValue,
    ushort FunctionIndex,
    uint Param1,
    uint Param2,
    long Offset);

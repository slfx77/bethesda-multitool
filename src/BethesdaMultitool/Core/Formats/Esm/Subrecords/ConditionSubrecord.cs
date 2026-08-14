namespace BethesdaMultitool.Core.Formats.Esm.Subrecords;

/// <summary>
///     CTDA subrecord - Condition data.
///     Used in quests, dialogues, packages, recipes, and magic effects. The comparison field is a
///     tagged union: Type bit 0x04 means the four bytes contain a GLOB FormID rather than a float.
/// </summary>
public record ConditionSubrecord(
    byte Type,
    uint ComparisonRawBits,
    ushort FunctionIndex,
    uint Param1,
    uint Param2,
    long Offset,
    uint? RunOn = null,
    uint? ReferenceStorage = null,
    int? Parameter3 = null)
{
    /// <summary>Comparison operator encoded in bits 5-7 of <see cref="Type" />.</summary>
    public byte Operator => (byte)((Type >> 5) & 0x07);

    /// <summary>The float projection of the comparison union's exact four serialized bytes.</summary>
    public float ComparisonValue => BitConverter.UInt32BitsToSingle(ComparisonRawBits);

    /// <summary>Whether the comparison union contains a GLOB FormID instead of a numeric float.</summary>
    public bool UsesGlobalComparison => (Type & 0x04) != 0;

    /// <summary>The numeric comparison value, or null when the comparison is a GLOB FormID.</summary>
    public float? NumericComparisonValue => UsesGlobalComparison ? null : ComparisonValue;

    /// <summary>The comparison GLOB FormID, including zero, or null for a numeric comparison.</summary>
    public uint? ComparisonGlobalFormId => UsesGlobalComparison ? ComparisonRawBits : null;
}

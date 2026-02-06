namespace FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

/// <summary>
///     NAME subrecord - base object FormID reference.
///     Common in REFR, ACHR, ACRE records.
/// </summary>
public record NameSubrecord(uint BaseFormId, long Offset, bool IsBigEndian);

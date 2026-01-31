namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;

/// <summary>
///     Generic FormID reference subrecord (SCRI, ENAM, SNAM, QNAM, etc.).
/// </summary>
public record FormIdSubrecord(string SubrecordType, uint FormId, long Offset);

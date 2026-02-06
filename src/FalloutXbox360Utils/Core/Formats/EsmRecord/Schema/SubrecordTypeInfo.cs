using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Schema;

/// <summary>
///     Information about a subrecord type.
/// </summary>
public record SubrecordTypeInfo(string Name, SubrecordDataType DataType, int? FixedSize = null);

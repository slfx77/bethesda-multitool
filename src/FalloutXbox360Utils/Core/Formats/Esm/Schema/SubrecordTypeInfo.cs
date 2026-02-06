using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Schema;

/// <summary>
///     Information about a subrecord type.
/// </summary>
public record SubrecordTypeInfo(string Name, SubrecordDataType DataType, int? FixedSize = null);

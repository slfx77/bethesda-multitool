using BethesdaMultitool.Core.Formats.Esm.Enums;

namespace BethesdaMultitool.Core.Formats.Esm.Schema;

/// <summary>
///     Information about a subrecord type.
/// </summary>
public record SubrecordTypeInfo(string Name, SubrecordDataType DataType, int? FixedSize = null);

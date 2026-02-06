using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Schema;

/// <summary>
///     Information about a main record type.
/// </summary>
public record RecordTypeInfo(string Name, RecordCategory Category, int? FormTypeId = null);

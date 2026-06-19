using BethesdaMultitool.Core.Formats.Esm.Enums;

namespace BethesdaMultitool.Core.Formats.Esm.Schema;

/// <summary>
///     Information about a main record type.
/// </summary>
public record RecordTypeInfo(string Name, RecordCategory Category, int? FormTypeId = null);

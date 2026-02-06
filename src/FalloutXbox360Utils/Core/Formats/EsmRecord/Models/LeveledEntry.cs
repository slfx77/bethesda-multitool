namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Leveled list entry (LVLO subrecord data).
/// </summary>
public record LeveledEntry(ushort Level, uint FormId, ushort Count);

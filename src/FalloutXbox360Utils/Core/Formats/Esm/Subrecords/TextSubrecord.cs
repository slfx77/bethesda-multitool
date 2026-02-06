namespace FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

/// <summary>
///     Generic text-containing subrecord (FULL, DESC, MODL, ICON, etc.).
/// </summary>
public record TextSubrecord(string SubrecordType, string Text, long Offset);

namespace FalloutXbox360Utils.Core.Utils;

/// <summary>
///     Subrecord structure parsed from ESM record data.
/// </summary>
/// <param name="Signature">4-character subrecord type signature (e.g., "EDID", "FULL").</param>
/// <param name="DataOffset">Byte offset to subrecord data (after 6-byte header).</param>
/// <param name="DataLength">Length of subrecord data in bytes.</param>
public readonly record struct ParsedSubrecord(string Signature, int DataOffset, int DataLength);

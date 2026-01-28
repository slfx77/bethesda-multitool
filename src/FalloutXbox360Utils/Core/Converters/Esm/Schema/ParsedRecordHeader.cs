namespace FalloutXbox360Utils.Core.Converters.Esm.Schema;

/// <summary>
///     Parsed record header data.
/// </summary>
/// <param name="Signature">4-character record type signature.</param>
/// <param name="DataSize">Size of record data (excluding header).</param>
/// <param name="Flags">Record flags.</param>
/// <param name="FormId">Unique form identifier.</param>
/// <param name="Timestamp">Modification timestamp.</param>
/// <param name="VcsInfo">Version control info.</param>
/// <param name="Version">Record version.</param>
public readonly record struct ParsedRecordHeader(
    string Signature,
    uint DataSize,
    uint Flags,
    uint FormId,
    uint Timestamp,
    ushort VcsInfo,
    ushort Version)
{
    /// <summary>
    ///     Checks if this record is compressed.
    /// </summary>
    public bool IsCompressed => (Flags & 0x00040000) != 0;
}

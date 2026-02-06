namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     Shared constants for ESM conversion operations.
/// </summary>
internal static class EsmConverterConstants
{
    /// <summary>
    ///     Main record header size.
    /// </summary>
    public const int MainRecordHeaderSize = 20;

    /// <summary>
    ///     GRUP header size.
    /// </summary>
    public const int GrupHeaderSize = 24;

    /// <summary>
    ///     Subrecord header size (4-byte signature + 2-byte size).
    /// </summary>
    public const int SubrecordHeaderSize = 6;

    /// <summary>
    ///     Record flag indicating the record data is zlib-compressed.
    /// </summary>
    public const uint CompressedFlag = 0x00040000;
}

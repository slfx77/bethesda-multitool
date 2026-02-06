namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Schema definitions for ESM record and GRUP headers.
///     Used by <see cref="RecordHeaderProcessor" /> for endian conversion.
/// </summary>
public static class RecordHeaderSchema
{
    // ========================================================================
    // Record Header Layout (24 bytes)
    // ========================================================================

    /// <summary>Total size of a record header.</summary>
    public const int RecordHeaderSize = 24;

    /// <summary>Offset of Signature field (4 bytes, reversed for big-endian).</summary>
    public const int RecordSignatureOffset = 0;

    /// <summary>Offset of DataSize field (4 bytes, size of record data excluding header).</summary>
    public const int RecordDataSizeOffset = 4;

    /// <summary>Offset of Flags field (4 bytes, record flags including compression bit).</summary>
    public const int RecordFlagsOffset = 8;

    /// <summary>Offset of FormId field (4 bytes, unique record identifier).</summary>
    public const int RecordFormIdOffset = 12;

    /// <summary>Offset of Timestamp field (4 bytes, modification timestamp).</summary>
    public const int RecordTimestampOffset = 16;

    /// <summary>Offset of VcsInfo field (2 bytes, version control info).</summary>
    public const int RecordVcsInfoOffset = 20;

    /// <summary>Offset of Version field (2 bytes, record version).</summary>
    public const int RecordVersionOffset = 22;

    // ========================================================================
    // GRUP Header Layout (24 bytes)
    // ========================================================================

    /// <summary>Total size of a GRUP header.</summary>
    public const int GrupHeaderSize = 24;

    /// <summary>Offset of Signature field (4 bytes, "GRUP" reversed for big-endian).</summary>
    public const int GrupSignatureOffset = 0;

    /// <summary>Offset of Size field (4 bytes, total GRUP size including header).</summary>
    public const int GrupSizeOffset = 4;

    /// <summary>Offset of Label field (4 bytes, meaning depends on GRUP type).</summary>
    public const int GrupLabelOffset = 8;

    /// <summary>Offset of Type field (4 bytes, GRUP type: 0=Top-level, 1=World Children, etc.).</summary>
    public const int GrupTypeOffset = 12;

    /// <summary>Offset of Stamp field (4 bytes, timestamp).</summary>
    public const int GrupStampOffset = 16;

    /// <summary>Offset of Unknown field (4 bytes).</summary>
    public const int GrupUnknownOffset = 20;
}

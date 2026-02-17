namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Generic ESM record for types that share a common structure:
///     EDID + FULL + MODL + OBND + type-specific subrecords.
///     Used for MSTT, TACT, CAMS, ANIO, IPDS, EFSH, RGDL, LSCR,
///     ASPC, MSET, CHIP, CSNO, DOBJ, ADDN, TREE, IMAD.
/// </summary>
public record GenericEsmRecord
{
    /// <summary>FormID of the record.</summary>
    public uint FormId { get; init; }

    /// <summary>4-character ESM record type signature (e.g., "MSTT", "CAMS").</summary>
    public string RecordType { get; init; } = "";

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (FULL subrecord).</summary>
    public string? FullName { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>
    ///     Type-specific subrecord fields parsed via SubrecordDataReader schemas
    ///     or stored as raw byte arrays. Keys are subrecord signatures.
    /// </summary>
    public Dictionary<string, object?> Fields { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Misc Item from memory dump.
///     Aggregates data from MISC main record header, DATA (8 bytes).
/// </summary>
public record ReconstructedMiscItem
{
    /// <summary>FormID of the misc item record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (8 bytes)
    /// <summary>Base value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

/// <summary>
///     Parsed Key record.
/// </summary>
public record KeyRecord
{
    /// <summary>FormID of the key record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weight.</summary>
    public float Weight { get; init; }

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Inventory image path from ICON subrecord.</summary>
    public string? IconPath { get; init; }

    /// <summary>Message icon path from MICO subrecord.</summary>
    public string? MessageIconPath { get; init; }

    /// <summary>Texture hash data from MODT subrecord (opaque bytes — engine validates).</summary>
    public byte[]? TextureHashData { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

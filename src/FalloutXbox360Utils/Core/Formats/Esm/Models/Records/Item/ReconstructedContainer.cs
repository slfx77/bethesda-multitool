namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Container from memory dump.
/// </summary>
public record ReconstructedContainer
{
    /// <summary>FormID of the container record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Container flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Whether this container respawns.</summary>
    public bool Respawns => (Flags & 0x02) != 0;

    /// <summary>Contents of the container.</summary>
    public List<InventoryItem> Contents { get; init; } = [];

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Script FormID.</summary>
    public uint? Script { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

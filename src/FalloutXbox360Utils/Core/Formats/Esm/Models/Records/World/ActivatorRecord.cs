namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Activator (ACTI) record.
///     A world object the player can interact with (switches, levers, crafting stations, etc.).
/// </summary>
public record ActivatorRecord
{
    /// <summary>FormID of the activator record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Script FormID (SCRI subrecord).</summary>
    public uint? Script { get; init; }

    /// <summary>Activation sound FormID (SNAM subrecord).</summary>
    public uint? ActivationSoundFormId { get; init; }

    /// <summary>Radio station FormID (RNAM subrecord).</summary>
    public uint? RadioStationFormId { get; init; }

    /// <summary>Water type FormID (WNAM subrecord).</summary>
    public uint? WaterTypeFormId { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

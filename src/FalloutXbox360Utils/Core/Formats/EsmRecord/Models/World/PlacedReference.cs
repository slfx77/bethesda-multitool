using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Placed object reference from REFR/ACHR/ACRE records.
/// </summary>
public record PlacedReference
{
    /// <summary>FormID of the placed reference.</summary>
    public uint FormId { get; init; }

    /// <summary>FormID of the base object being placed.</summary>
    public uint BaseFormId { get; init; }

    /// <summary>Editor ID of the base object (if resolved).</summary>
    public string? BaseEditorId { get; init; }

    /// <summary>Record type (REFR, ACHR, or ACRE).</summary>
    public string RecordType { get; init; } = "REFR";

    /// <summary>X position in world coordinates.</summary>
    public float X { get; init; }

    /// <summary>Y position in world coordinates.</summary>
    public float Y { get; init; }

    /// <summary>Z position in world coordinates.</summary>
    public float Z { get; init; }

    /// <summary>X rotation in radians.</summary>
    public float RotX { get; init; }

    /// <summary>Y rotation in radians.</summary>
    public float RotY { get; init; }

    /// <summary>Z rotation in radians.</summary>
    public float RotZ { get; init; }

    /// <summary>Scale factor (1.0 = normal).</summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>Owner FormID (XOWN subrecord).</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>Enable parent FormID (XESP subrecord).</summary>
    public uint? EnableParentFormId { get; init; }

    /// <summary>Whether this is a map marker (has XMRK subrecord).</summary>
    public bool IsMapMarker { get; init; }

    /// <summary>Map marker type (0=None..14=Vault).</summary>
    public MapMarkerType? MarkerType { get; init; }

    /// <summary>Map marker display name (FULL subrecord).</summary>
    public string? MarkerName { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Extracted REFR (placed object) with full placement data.
///     Links base object to position for visualization.
/// </summary>
public record ExtractedRefrRecord
{
    /// <summary>Parent main record information.</summary>
    public required DetectedMainRecord Header { get; init; }

    /// <summary>NAME - Base object FormID being placed.</summary>
    public uint BaseFormId { get; init; }

    /// <summary>DATA - Position in world coordinates.</summary>
    public PositionSubrecord? Position { get; init; }

    /// <summary>XSCL - Scale factor (1.0 = normal).</summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>XOWN - Owner FormID.</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>XTEL - Destination door FormID (teleport target).</summary>
    public uint? DestinationDoorFormId { get; init; }

    /// <summary>Parent cell FormID (if known).</summary>
    public uint? ParentCellFormId { get; init; }

    /// <summary>XESP - Enable Parent FormID.</summary>
    public uint? EnableParentFormId { get; init; }

    /// <summary>XESP - Enable Parent Flags (bit 0 = "Set Enable State to Opposite of Parent").</summary>
    public byte? EnableParentFlags { get; init; }

    /// <summary>Editor ID of base object (if resolved).</summary>
    public string? BaseEditorId { get; init; }

    /// <summary>XMRK - Whether this is a map marker.</summary>
    public bool IsMapMarker { get; init; }

    /// <summary>TNAM - Map marker type enum value.</summary>
    public ushort? MarkerType { get; init; }

    /// <summary>FULL - Map marker display name.</summary>
    public string? MarkerName { get; init; }
}

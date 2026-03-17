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

    /// <summary>XRDS - Activation/placement radius.</summary>
    public float? Radius { get; init; }

    /// <summary>XOWN - Owner FormID.</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>XEZN - Encounter zone FormID.</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>XLOC - Lock level.</summary>
    public byte? LockLevel { get; init; }

    /// <summary>XLOC - Lock key FormID.</summary>
    public uint? LockKeyFormId { get; init; }

    /// <summary>XLOC - Lock flags.</summary>
    public byte? LockFlags { get; init; }

    /// <summary>XLOC - Number of failed attempts tracked by the runtime lock state.</summary>
    public uint? LockNumTries { get; init; }

    /// <summary>XLOC - Number of times the object has been unlocked.</summary>
    public uint? LockTimesUnlocked { get; init; }

    /// <summary>XTEL - Destination door FormID (teleport target).</summary>
    public uint? DestinationDoorFormId { get; init; }

    /// <summary>Parent cell FormID (if known).</summary>
    public uint? ParentCellFormId { get; init; }

    /// <summary>Whether the parent cell is an interior cell (from runtime cCellFlags bit 0).</summary>
    public bool? ParentCellIsInterior { get; init; }

    /// <summary>Runtime persistent cell FormID from ExtraPersistentCell when present.</summary>
    public uint? PersistentCellFormId { get; init; }

    /// <summary>Runtime start transform from ExtraStartingPosition when present.</summary>
    public PositionSubrecord? StartingPosition { get; init; }

    /// <summary>Runtime starting world/cell FormID from ExtraStartingWorldOrCell when present.</summary>
    public uint? StartingWorldOrCellFormId { get; init; }

    /// <summary>Runtime package start location from ExtraPackageStartLocation when present.</summary>
    public RuntimePackageStartLocation? PackageStartLocation { get; init; }

    /// <summary>Runtime merchant container reference FormID from ExtraMerchantContainer when present.</summary>
    public uint? MerchantContainerFormId { get; init; }

    /// <summary>Runtime original spawn base FormID from ExtraLeveledCreature when present.</summary>
    public uint? LeveledCreatureOriginalBaseFormId { get; init; }

    /// <summary>Runtime spawn template FormID from ExtraLeveledCreature when present.</summary>
    public uint? LeveledCreatureTemplateFormId { get; init; }

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

    /// <summary>XLKR - Linked reference keyword FormID when present on the 8-byte variant.</summary>
    public uint? LinkedRefKeywordFormId { get; init; }

    /// <summary>XLKR - Linked reference FormID.</summary>
    public uint? LinkedRefFormId { get; init; }

    /// <summary>Runtime-linked child refs from ExtraLinkedRefChildren when present.</summary>
    public List<uint> LinkedRefChildrenFormIds { get; init; } = [];
}

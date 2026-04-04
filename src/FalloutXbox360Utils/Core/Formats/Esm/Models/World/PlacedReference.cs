using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.World;

/// <summary>
///     Placed object reference from REFR/ACHR/ACRE records.
/// </summary>
public record PlacedReference
{
    /// <summary>Object bounds from the base object's OBND subrecord (if resolved).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Model path from the base object's MODL subrecord (if resolved).</summary>
    public string? ModelPath { get; init; }

    /// <summary>FormID of the placed reference.</summary>
    public uint FormId { get; init; }

    /// <summary>FormID of the base object being placed.</summary>
    public uint BaseFormId { get; init; }

    /// <summary>Editor ID of the base object (if resolved).</summary>
    public string? BaseEditorId { get; init; }

    /// <summary>Editor ID of the placed reference itself (from EDID subrecord or ExtraEditorID at runtime).</summary>
    public string? EditorId { get; init; }

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

    /// <summary>Activation or placement radius from XRDS/ExtraRadius.</summary>
    public float? Radius { get; init; }

    /// <summary>Item stack count from XCNT subrecord / ExtraCount.</summary>
    public short? Count { get; init; }

    /// <summary>Owner FormID (XOWN subrecord).</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>Encounter zone FormID (XEZN subrecord / ExtraEncounterZone).</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>Lock level from XLOC/ExtraLock.</summary>
    public byte? LockLevel { get; init; }

    /// <summary>Lock key FormID from XLOC/ExtraLock.</summary>
    public uint? LockKeyFormId { get; init; }

    /// <summary>Lock flags from XLOC/ExtraLock.</summary>
    public byte? LockFlags { get; init; }

    /// <summary>Lock try count from XLOC/ExtraLock.</summary>
    public uint? LockNumTries { get; init; }

    /// <summary>Unlock count from XLOC/ExtraLock.</summary>
    public uint? LockTimesUnlocked { get; init; }

    /// <summary>Enable parent FormID (XESP subrecord).</summary>
    public uint? EnableParentFormId { get; init; }

    /// <summary>Persistent cell FormID from runtime ExtraPersistentCell when available.</summary>
    public uint? PersistentCellFormId { get; init; }

    /// <summary>Runtime start transform from ExtraStartingPosition when available.</summary>
    public PositionSubrecord? StartingPosition { get; init; }

    /// <summary>Runtime starting world/cell FormID from ExtraStartingWorldOrCell when available.</summary>
    public uint? StartingWorldOrCellFormId { get; init; }

    /// <summary>Runtime package start location from ExtraPackageStartLocation when available.</summary>
    public RuntimePackageStartLocation? PackageStartLocation { get; init; }

    /// <summary>Runtime merchant container reference FormID from ExtraMerchantContainer when available.</summary>
    public uint? MerchantContainerFormId { get; init; }

    /// <summary>Runtime original spawn base FormID from ExtraLeveledCreature when available.</summary>
    public uint? LeveledCreatureOriginalBaseFormId { get; init; }

    /// <summary>Runtime spawn template FormID from ExtraLeveledCreature when available.</summary>
    public uint? LeveledCreatureTemplateFormId { get; init; }

    /// <summary>Destination door FormID from XTEL (for door references).</summary>
    public uint? DestinationDoorFormId { get; init; }

    /// <summary>Destination cell FormID resolved from door teleport (XTEL → cell lookup).</summary>
    public uint? DestinationCellFormId { get; init; }

    /// <summary>Whether this is a map marker (has XMRK subrecord).</summary>
    public bool IsMapMarker { get; init; }

    /// <summary>Map marker type (0=None..14=Vault).</summary>
    public MapMarkerType? MarkerType { get; init; }

    /// <summary>Map marker display name (FULL subrecord).</summary>
    public string? MarkerName { get; init; }

    /// <summary>Whether this is a persistent reference (flag 0x0400 on main record header).</summary>
    public bool IsPersistent { get; init; }

    /// <summary>Whether this record has the Initially Disabled flag (0x0800) on its main record header.</summary>
    public bool IsInitiallyDisabled { get; init; }

    /// <summary>Enable parent flags byte from XESP subrecord (bit 0 = opposite state).</summary>
    public byte? EnableParentFlags { get; init; }

    /// <summary>XLKR keyword FormID when the 8-byte linked-ref variant is present.</summary>
    public uint? LinkedRefKeywordFormId { get; init; }

    /// <summary>XLKR - Linked reference FormID for spawn resolution (PLDT type 12).</summary>
    public uint? LinkedRefFormId { get; init; }

    /// <summary>Runtime-linked child refs derived from ExtraLinkedRefChildren.</summary>
    public List<uint> LinkedRefChildrenFormIds { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>How this ref was assigned to its cell during DMP linkage (ParentCell, GridMap, or Virtual).</summary>
    public string? AssignmentSource { get; init; }
}

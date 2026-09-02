using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Models.World;

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

    /// <summary>XRDS - signed ExtraRadius adjustment.</summary>
    public float? Radius { get; init; }

    /// <summary>XRDO - radio broadcast configuration from ExtraRadioData (BSExtraData type 0x68).</summary>
    public RadioData? RadioData { get; init; }

    /// <summary>XOWN - Owner FormID.</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>XEZN - Encounter zone FormID.</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>
    ///     XMSP - Material swap (MSWP) FormID. FO4/FO76 only: re-skins this placement's mesh by
    ///     substituting whole <c>.bgsm</c> materials at decode time.
    /// </summary>
    public uint? MaterialSwapFormId { get; init; }

    /// <summary>XEMI - external-emittance REGN/LIGH FormID used by effect shader properties.</summary>
    public uint? EmittanceFormId { get; init; }

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

    /// <summary>
    ///     XTEL - Teleport position + rotation (6 floats at offsets 4-27 of the 32-byte
    ///     subrecord). Populated from ESM XTEL bytes by both parser paths. Null for
    ///     runtime-only refs and for the 4-byte XTEL variant (legacy format with only
    ///     the door FormID).
    /// </summary>
    public PositionSubrecord? TeleportPosRot { get; init; }

    /// <summary>XTEL flags byte (offset 28 of the 32-byte subrecord).</summary>
    public byte? TeleportFlags { get; init; }

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

    /// <summary>XCNT - Item stack count from ExtraCount (int16 at BSExtraData+12).</summary>
    public short? Count { get; init; }

    /// <summary>XESP - Enable Parent FormID.</summary>
    public uint? EnableParentFormId { get; init; }

    /// <summary>XESP - Enable Parent Flags (bit 0 = "Set Enable State to Opposite of Parent").</summary>
    public byte? EnableParentFlags { get; init; }

    /// <summary>XSRF - Special Rendering Flags (FO3/FNV; 0x2 = Imposter, 0x4 = Use Full Shader in LOD).</summary>
    public uint? SpecialRenderingFlags { get; init; }

    /// <summary>Editor ID of base object (if resolved).</summary>
    public string? BaseEditorId { get; init; }

    /// <summary>EDID - Editor ID of the placed reference itself (from ExtraEditorID at runtime, EDID subrecord in ESM).</summary>
    public string? EditorId { get; init; }

    /// <summary>XMRK - Whether this is a map marker.</summary>
    public bool IsMapMarker { get; init; }

    /// <summary>TNAM - Map marker type enum value.</summary>
    public ushort? MarkerType { get; init; }

    /// <summary>
    ///     FULL - Map marker display name (eager null-term decode; garbage for a localized plugin's
    ///     4-byte string ID — prefer resolving <see cref="MarkerNameRaw" /> via the .STRINGS table).
    /// </summary>
    public string? MarkerName { get; init; }

    /// <summary>
    ///     Raw FULL subrecord bytes, resolved late against the localized .STRINGS table (the table isn't
    ///     loaded during the descriptor scan). Null for records without a FULL.
    /// </summary>
    public byte[]? MarkerNameRaw { get; init; }

    /// <summary>XLKR - Linked reference keyword FormID when present on the 8-byte variant.</summary>
    public uint? LinkedRefKeywordFormId { get; init; }

    /// <summary>XLKR - Linked reference FormID.</summary>
    public uint? LinkedRefFormId { get; init; }

    /// <summary>Runtime-linked child refs from ExtraLinkedRefChildren when present.</summary>
    /// <summary>
    ///     Runtime-linked child refs (DMP ExtraLinkedRefChildren; never populated by the ESM
    ///     descriptor scanner). <c>IReadOnlyList</c> so the empty default costs no allocation.
    /// </summary>
    public IReadOnlyList<uint> LinkedRefChildrenFormIds { get; init; } = [];

    /// <summary>XBSD - Fallout 4-family procedural bendable-spline parameters.</summary>
    public BendableSplinePlacementData? BendableSpline { get; init; }

    /// <summary>Room/portal/occlusion structural subrecords carried by this placed marker reference.</summary>
    public PlacedReferenceStructuralData? StructuralData { get; init; }
}

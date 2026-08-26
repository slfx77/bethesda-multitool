using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Models.World;

/// <summary>
///     Placed object reference from REFR/ACHR/ACRE records.
///     <para>
///         The fields below are the ones nearly every reference carries. Everything rarer — locks,
///         teleports, ownership, enable parents, marker detail, the runtime-only DMP fields — lives
///         in <see cref="PlacedReferenceExtras" />, reached through the properties further down,
///         which behave exactly as the inline fields did. See that type for why: the group cost
///         about 244 bytes of mostly-null padding on every one of Fallout 76's 5.1M references.
///     </para>
/// </summary>
public record PlacedReference
{
    /// <summary>
    ///     Seed for the first extra assigned. Never mutated — every accessor below produces a new
    ///     instance via <c>with</c> — so one shared instance is safe.
    /// </summary>
    private static readonly PlacedReferenceExtras EmptyExtras = new();

    /// <summary>
    ///     Null until this reference carries at least one extra. A record's generated copy
    ///     constructor copies this reference, which is safe only because
    ///     <see cref="PlacedReferenceExtras" /> is immutable — see its remarks.
    /// </summary>
    private readonly PlacedReferenceExtras? _extras;

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

    /// <summary>Whether this is a map marker (has XMRK subrecord).</summary>
    public bool IsMapMarker { get; init; }

    /// <summary>Whether this is a persistent reference (flag 0x0400 on main record header).</summary>
    public bool IsPersistent { get; init; }

    /// <summary>Whether this record has the Initially Disabled flag (0x0800) on its main record header.</summary>
    public bool IsInitiallyDisabled { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>
    ///     The side object, or null when this reference carries no extras at all. Exposed so callers
    ///     that need many extras at once — exporters, the planner — can read them through one field
    ///     access instead of one per property.
    /// </summary>
    public PlacedReferenceExtras? Extras => _extras;

    /// <summary>Signed radius adjustment from XRDS/ExtraRadius.</summary>
    public float? Radius
    {
        get => _extras?.Radius;
        init { if (value is not null || _extras is not null) _extras = Seed with { Radius = value }; }
    }

    /// <summary>Item stack count from XCNT subrecord / ExtraCount.</summary>
    public short? Count
    {
        get => _extras?.Count;
        init { if (value is not null || _extras is not null) _extras = Seed with { Count = value }; }
    }

    /// <summary>
    ///     Radio broadcast configuration from the XRDO subrecord / ExtraRadioData. Present only on
    ///     references whose base form is a Radio-Station-flagged TACT; every such reference in
    ///     retail FalloutNV.esm carries one.
    /// </summary>
    public RadioData? RadioData
    {
        get => _extras?.RadioData;
        init { if (value is not null || _extras is not null) _extras = Seed with { RadioData = value }; }
    }

    /// <summary>Owner FormID (XOWN subrecord).</summary>
    public uint? OwnerFormId
    {
        get => _extras?.OwnerFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { OwnerFormId = value }; }
    }

    /// <summary>Encounter zone FormID (XEZN subrecord / ExtraEncounterZone).</summary>
    public uint? EncounterZoneFormId
    {
        get => _extras?.EncounterZoneFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { EncounterZoneFormId = value }; }
    }

    /// <summary>
    ///     Material swap (MSWP) FormID from the XMSP subrecord. FO4/FO76 only: the 3D viewer
    ///     resolves it to BNAM→SNAM material substitutions and bakes this placement's mesh as its own
    ///     re-skinned variant, so alpha/two-sided/specular flow from the replacement materials.
    /// </summary>
    public uint? MaterialSwapFormId
    {
        get => _extras?.MaterialSwapFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { MaterialSwapFormId = value }; }
    }

    /// <summary>
    ///     XEMI external-emittance FormID. External-emittance shader properties source their
    ///     color from this placement link (normally REGN, sometimes LIGH) instead of the NIF's
    ///     material emissive multiplier.
    /// </summary>
    public uint? EmittanceFormId
    {
        get => _extras?.EmittanceFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { EmittanceFormId = value }; }
    }

    /// <summary>Lock level from XLOC/ExtraLock.</summary>
    public byte? LockLevel
    {
        get => _extras?.LockLevel;
        init { if (value is not null || _extras is not null) _extras = Seed with { LockLevel = value }; }
    }

    /// <summary>Lock key FormID from XLOC/ExtraLock.</summary>
    public uint? LockKeyFormId
    {
        get => _extras?.LockKeyFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { LockKeyFormId = value }; }
    }

    /// <summary>Lock flags from XLOC/ExtraLock.</summary>
    public byte? LockFlags
    {
        get => _extras?.LockFlags;
        init { if (value is not null || _extras is not null) _extras = Seed with { LockFlags = value }; }
    }

    /// <summary>Lock try count from XLOC/ExtraLock.</summary>
    public uint? LockNumTries
    {
        get => _extras?.LockNumTries;
        init { if (value is not null || _extras is not null) _extras = Seed with { LockNumTries = value }; }
    }

    /// <summary>Unlock count from XLOC/ExtraLock.</summary>
    public uint? LockTimesUnlocked
    {
        get => _extras?.LockTimesUnlocked;
        init { if (value is not null || _extras is not null) _extras = Seed with { LockTimesUnlocked = value }; }
    }

    /// <summary>Enable parent FormID (XESP subrecord).</summary>
    public uint? EnableParentFormId
    {
        get => _extras?.EnableParentFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { EnableParentFormId = value }; }
    }

    /// <summary>Enable parent flags byte from XESP subrecord (bit 0 = opposite state).</summary>
    public byte? EnableParentFlags
    {
        get => _extras?.EnableParentFlags;
        init { if (value is not null || _extras is not null) _extras = Seed with { EnableParentFlags = value }; }
    }

    /// <summary>Persistent cell FormID from runtime ExtraPersistentCell when available.</summary>
    public uint? PersistentCellFormId
    {
        get => _extras?.PersistentCellFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { PersistentCellFormId = value }; }
    }

    /// <summary>Runtime start transform from ExtraStartingPosition when available.</summary>
    public PositionSubrecord? StartingPosition
    {
        get => _extras?.StartingPosition;
        init { if (value is not null || _extras is not null) _extras = Seed with { StartingPosition = value }; }
    }

    /// <summary>Runtime starting world/cell FormID from ExtraStartingWorldOrCell when available.</summary>
    public uint? StartingWorldOrCellFormId
    {
        get => _extras?.StartingWorldOrCellFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { StartingWorldOrCellFormId = value }; }
    }

    /// <summary>Runtime package start location from ExtraPackageStartLocation when available.</summary>
    public RuntimePackageStartLocation? PackageStartLocation
    {
        get => _extras?.PackageStartLocation;
        init { if (value is not null || _extras is not null) _extras = Seed with { PackageStartLocation = value }; }
    }

    /// <summary>Runtime merchant container reference FormID from ExtraMerchantContainer when available.</summary>
    public uint? MerchantContainerFormId
    {
        get => _extras?.MerchantContainerFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { MerchantContainerFormId = value }; }
    }

    /// <summary>Runtime original spawn base FormID from ExtraLeveledCreature when available.</summary>
    public uint? LeveledCreatureOriginalBaseFormId
    {
        get => _extras?.LeveledCreatureOriginalBaseFormId;
        init
        {
            if (value is not null || _extras is not null)
            {
                _extras = Seed with { LeveledCreatureOriginalBaseFormId = value };
            }
        }
    }

    /// <summary>Runtime spawn template FormID from ExtraLeveledCreature when available.</summary>
    public uint? LeveledCreatureTemplateFormId
    {
        get => _extras?.LeveledCreatureTemplateFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { LeveledCreatureTemplateFormId = value }; }
    }

    /// <summary>Destination door FormID from XTEL (for door references).</summary>
    public uint? DestinationDoorFormId
    {
        get => _extras?.DestinationDoorFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { DestinationDoorFormId = value }; }
    }

    /// <summary>Destination cell FormID resolved from door teleport (XTEL → cell lookup).</summary>
    public uint? DestinationCellFormId
    {
        get => _extras?.DestinationCellFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { DestinationCellFormId = value }; }
    }

    /// <summary>
    ///     XTEL teleport position + rotation (6 floats at offsets 4-27 in the on-disk subrecord).
    ///     Populated by both ESM parser paths (EsmWorldExtractor + EsmDataExtractor) for refs
    ///     carrying a 32-byte XTEL. Null for runtime-only refs — DoorTeleportData runtime
    ///     extraction is not implemented (would require pointer-chase from the REFR struct's
    ///     ExtraTeleport entry).
    /// </summary>
    public PositionSubrecord? TeleportPosRot
    {
        get => _extras?.TeleportPosRot;
        init { if (value is not null || _extras is not null) _extras = Seed with { TeleportPosRot = value }; }
    }

    /// <summary>XTEL flags byte (offset 28). Bit 0 = unknown; rest reserved.</summary>
    public byte? TeleportFlags
    {
        get => _extras?.TeleportFlags;
        init { if (value is not null || _extras is not null) _extras = Seed with { TeleportFlags = value }; }
    }

    /// <summary>Map marker type (0=None..14=Vault).</summary>
    public MapMarkerType? MarkerType
    {
        get => _extras?.MarkerType;
        init { if (value is not null || _extras is not null) _extras = Seed with { MarkerType = value }; }
    }

    /// <summary>Map marker display name (FULL subrecord).</summary>
    public string? MarkerName
    {
        get => _extras?.MarkerName;
        init { if (value is not null || _extras is not null) _extras = Seed with { MarkerName = value }; }
    }

    /// <summary>
    ///     FormID of the persistent cell that originally owned this ref before it was
    ///     redistributed by world position to its real exterior tile. Null when the ref
    ///     was placed directly into its parent cell. Used by reports/exporters to
    ///     reconstruct the "persistent only" view after redistribution.
    /// </summary>
    public uint? OriginCellFormId
    {
        get => _extras?.OriginCellFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { OriginCellFormId = value }; }
    }

    /// <summary>
    ///     XSRF Special Rendering Flags (FO3/FNV): bit 1 (0x2) = Imposter, bit 2 (0x4) = Use Full
    ///     Shader in LOD. Imposter references are stand-ins the engine draws only for scripted
    ///     vantage set-pieces (e.g. `vLegateCampFortFireFX`, the ending's burning Legate camp seen
    ///     from Hoover Dam) — never during normal play, even though the REFR itself is enabled.
    /// </summary>
    public uint? SpecialRenderingFlags
    {
        get => _extras?.SpecialRenderingFlags;
        init { if (value is not null || _extras is not null) _extras = Seed with { SpecialRenderingFlags = value }; }
    }

    /// <summary>XLKR keyword FormID when the 8-byte linked-ref variant is present.</summary>
    public uint? LinkedRefKeywordFormId
    {
        get => _extras?.LinkedRefKeywordFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { LinkedRefKeywordFormId = value }; }
    }

    /// <summary>XLKR - Linked reference FormID for spawn resolution (PLDT type 12).</summary>
    public uint? LinkedRefFormId
    {
        get => _extras?.LinkedRefFormId;
        init { if (value is not null || _extras is not null) _extras = Seed with { LinkedRefFormId = value }; }
    }

    /// <summary>
    ///     Runtime-linked child refs derived from ExtraLinkedRefChildren (DMP-only; never populated
    ///     for ESM-parsed refs). Reads as the shared empty array when unset — a per-instance
    ///     <c>new List</c> here cost one allocation per placed ref (5.1M on Fallout 76) for a list
    ///     that is virtually always empty.
    /// </summary>
    public IReadOnlyList<uint> LinkedRefChildrenFormIds
    {
        get => _extras?.LinkedRefChildrenFormIds ?? [];
        init
        {
            // An empty assignment is the default, so it must not force an extras allocation.
            if (value.Count > 0 || _extras is not null)
            {
                _extras = Seed with { LinkedRefChildrenFormIds = value };
            }
        }
    }

    /// <summary>Room/portal/occlusion structural subrecords carried by this placed marker reference.</summary>
    public PlacedReferenceStructuralData? StructuralData
    {
        get => _extras?.StructuralData;
        init { if (value is not null || _extras is not null) _extras = Seed with { StructuralData = value }; }
    }

    /// <summary>How this ref was assigned to its cell during DMP linkage (ParentCell, GridMap, or Virtual).</summary>
    public string? AssignmentSource
    {
        get => _extras?.AssignmentSource;
        init { if (value is not null || _extras is not null) _extras = Seed with { AssignmentSource = value }; }
    }

    /// <summary>
    ///     True when <see cref="SpecialRenderingFlags" /> marks this reference an imposter —
    ///     authored invisible outside its scripted vantage, so viewers treat it like a disabled ref.
    ///     Null coalesces to 0 first: the lifted <c>null &amp; 0x2 != 0</c> comparison is TRUE in C#,
    ///     which would have flagged every ordinary ref.
    /// </summary>
    public bool IsImposter => ((SpecialRenderingFlags ?? 0) & 0x2) != 0;

    /// <summary>The extras to build the next one from — this reference's own, or the shared empty.</summary>
    private PlacedReferenceExtras Seed => _extras ?? EmptyExtras;
}

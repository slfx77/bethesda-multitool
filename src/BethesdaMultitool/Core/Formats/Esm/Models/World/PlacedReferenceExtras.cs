using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Models.World;

/// <summary>
///     The rarely-populated half of a <see cref="PlacedReference" />, held in a side object so that
///     the overwhelming majority of references — an ordinary STAT with a position, a scale and
///     nothing else — do not carry it at all.
///     <para>
///         <b>Why this exists.</b> These fields were 19 <c>uint?</c>, four <c>byte?</c>, a
///         <c>short?</c>, a <c>float?</c>, an enum nullable and six reference slots, sitting inline
///         on every reference. A <c>Nullable&lt;uint&gt;</c> costs 8 bytes after padding whether or
///         not it holds anything, so the group cost about 244 bytes on <em>every</em> placed
///         reference. Fallout 76's <c>SeventySix.esm</c> holds 5.1M of them, which is roughly 1.2 GB
///         of mostly-null padding in a load that was already memory-bound.
///     </para>
///     <para>
///         <b>Immutable on purpose, and that is the load-bearing decision.</b> <c>PlacedReference</c>
///         is a record, so <c>ref with { … }</c> is used freely across the codebase — and a record's
///         compiler-generated copy constructor copies the <em>reference</em> to this object. Were it
///         mutable, the <c>init</c> accessor that ran next would write through that shared reference
///         and silently corrupt the instance being copied FROM. Because it is immutable, each
///         accessor produces a fresh instance instead, which makes <c>with</c> correct without a
///         hand-written copy constructor listing all forty-odd properties — the kind that goes
///         quietly out of date the first time somebody adds a field.
///     </para>
///     <para>
///         The cost of that choice is a short-lived allocation per field assigned during
///         construction. That is gen-0 churn measured in hundreds of megabytes across a whole load,
///         against a gigabyte of <em>retained</em> memory removed — and retained memory is what was
///         driving the GC and paging thrash.
///     </para>
///     <para>
///         Every field here has real consumers; the constraint is compatibility, not coldness. They
///         are all load-, bake- or export-time reads, never per-frame, so the extra indirection
///         costs nothing on any hot path.
///     </para>
/// </summary>
public sealed record PlacedReferenceExtras
{
    /// <summary>Signed radius adjustment from XRDS/ExtraRadius.</summary>
    public float? Radius { get; init; }

    /// <summary>Item stack count from XCNT subrecord / ExtraCount.</summary>
    public short? Count { get; init; }

    /// <summary>Radio broadcast configuration from the XRDO subrecord / ExtraRadioData.</summary>
    public RadioData? RadioData { get; init; }

    /// <summary>Owner FormID (XOWN subrecord).</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>Encounter zone FormID (XEZN subrecord / ExtraEncounterZone).</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>Material swap (MSWP) FormID from the XMSP subrecord. FO4/FO76 only.</summary>
    public uint? MaterialSwapFormId { get; init; }

    /// <summary>XEMI external-emittance FormID.</summary>
    public uint? EmittanceFormId { get; init; }

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

    /// <summary>Enable parent flags byte from XESP subrecord (bit 0 = opposite state).</summary>
    public byte? EnableParentFlags { get; init; }

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

    /// <summary>XTEL teleport position + rotation (6 floats at offsets 4-27 in the on-disk subrecord).</summary>
    public PositionSubrecord? TeleportPosRot { get; init; }

    /// <summary>XTEL flags byte (offset 28). Bit 0 = unknown; rest reserved.</summary>
    public byte? TeleportFlags { get; init; }

    /// <summary>Map marker type (0=None..14=Vault).</summary>
    public MapMarkerType? MarkerType { get; init; }

    /// <summary>Map marker display name (FULL subrecord).</summary>
    public string? MarkerName { get; init; }

    /// <summary>FormID of the persistent cell that originally owned this ref before redistribution.</summary>
    public uint? OriginCellFormId { get; init; }

    /// <summary>XSRF Special Rendering Flags (FO3/FNV).</summary>
    public uint? SpecialRenderingFlags { get; init; }

    /// <summary>XLKR keyword FormID when the 8-byte linked-ref variant is present.</summary>
    public uint? LinkedRefKeywordFormId { get; init; }

    /// <summary>XLKR - Linked reference FormID for spawn resolution (PLDT type 12).</summary>
    public uint? LinkedRefFormId { get; init; }

    /// <summary>Runtime-linked child refs derived from ExtraLinkedRefChildren (DMP-only).</summary>
    public IReadOnlyList<uint>? LinkedRefChildrenFormIds { get; init; }

    /// <summary>Room/portal/occlusion structural subrecords carried by this placed marker reference.</summary>
    public PlacedReferenceStructuralData? StructuralData { get; init; }

    /// <summary>How this ref was assigned to its cell during DMP linkage (ParentCell, GridMap, or Virtual).</summary>
    public string? AssignmentSource { get; init; }
}

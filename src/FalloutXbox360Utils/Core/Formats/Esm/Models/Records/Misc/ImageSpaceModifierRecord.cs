namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Image Space Modifier (IMAD) record. Animated post-processing layer applied on top
///     of the base <see cref="ImageSpaceRecord" />; engine selects active IMADs for HDR
///     transitions, weapon-firing flashes, drug effects, etc. Missing the encoder strips
///     proto-only IMADs and causes the engine to load an undefined post-processing slot,
///     producing visible render mismatches and cell-entry instability in proto worldspaces.
///
///     This is a minimal encoder covering EDID + DNAM only. IMAD's frame-table subrecords
///     (BNAM/VNAM/TNAM/NAM3/RNAM/SNAM/UNAM and several IMAD-specific tint/blur arrays) are
///     out of scope for this phase — they animate timelines and need both a runtime reader
///     and frame-by-frame parsing not yet built. The minimal record still unblocks the
///     crash-on-missing case since the engine only requires EDID + DNAM (animatable flag +
///     duration) to instantiate the modifier slot.
/// </summary>
public record ImageSpaceModifierRecord
{
    public uint FormId { get; init; }

    public string? EditorId { get; init; }

    /// <summary>IMAD DNAM payload, 244 bytes. See <see cref="ImageSpaceModifierData" /> for layout.</summary>
    public ImageSpaceModifierData? Data { get; init; }

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}

/// <summary>
///     IMAD DNAM payload (244 bytes). Per
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordSchemaProcessor" />:
///     bytes 0..3 are uint32 already-LE on Xbox 360, bytes 4..243 are 60 little-endian
///     floats / uint32s that need byte-swapping on Xbox. The PC-output encoder simply
///     writes the canonical LE form.
/// </summary>
public record ImageSpaceModifierData
{
    /// <summary>Animatable flag (DNAM bytes 0..3, uint32).</summary>
    public uint AnimatableFlag { get; init; }

    /// <summary>Duration in seconds (DNAM bytes 4..7, float).</summary>
    public float Duration { get; init; }

    /// <summary>
    ///     Remaining payload (DNAM bytes 8..243, 59 × 4-byte values).
    ///     Each entry is either a uint32 count or a float (per fopdoc's per-slot
    ///     schema); the converter and encoder both treat them as little-endian
    ///     4-byte values without distinguishing — endian flips uniformly.
    /// </summary>
    public IReadOnlyList<uint> RawPayload { get; init; } = [];
}

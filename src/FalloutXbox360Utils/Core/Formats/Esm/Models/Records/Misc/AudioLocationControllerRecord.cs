namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Audio Location Controller (ALOC) record. Triggers a media set when the
///     player enters a region/cell matching the controller's conditions.
///     PDB struct: MediaLocationController (200 bytes, FormType 0x70).
/// </summary>
public record AudioLocationControllerRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Delay between entering the location and starting playback (uint32 at +52, NAM3).</summary>
    public uint LocationDelay { get; init; }

    /// <summary>Crossfade duration between layers (uint32 at +56, NAM4).</summary>
    public uint LayerTime { get; init; }

    /// <summary>Loop interval (uint32 at +60, NAM5).</summary>
    public uint LoopTime { get; init; }

    /// <summary>Initial play offset within the media set (uint32 at +64, NAM6).</summary>
    public uint MediaStartTime { get; init; }

    /// <summary>
    ///     Audio-emitting REFR the controller is bound to (resolved from <c>pAudioMarker</c>
    ///     pointer at PDB offset 120 in <c>MediaLocationController</c>). Null when the runtime
    ///     pointer is unset/dangling, or when the ALOC was read purely from ESM (the PC ESM
    ///     parser doesn't populate this — runtime-only enrichment).
    ///     Not currently emitted by the encoder: the FNV ALOC subrecord that carries this
    ///     link isn't disambiguated by the schema registry (HNAM/ZNAM/XNAM/YNAM/RNAM are all
    ///     labeled "Location Controller FormID" with no identifier of which is the marker
    ///     ref), so writing it back would risk overwriting an unrelated FormID slot. Surfaced
    ///     here for diff/report inspection until the slot is verified empirically.
    /// </summary>
    public uint? AudioMarkerFormId { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

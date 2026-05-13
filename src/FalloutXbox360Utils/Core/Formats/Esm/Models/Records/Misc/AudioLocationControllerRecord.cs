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

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

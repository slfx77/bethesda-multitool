namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Camera Path (CPTH) record. Sequences of camera shots played by quest
///     scripts. PDB struct: BGSCameraPath (72 bytes, FormType 0x5C).
/// </summary>
public record CameraPathRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>PATH_DATA byte at +56 (DATA subrecord).</summary>
    public byte Flags { get; init; }

    /// <summary>Parent camera path FormID (pParentPath pointer at +64, or ANAM on ESM side).</summary>
    public uint ParentPathFormId { get; init; }

    /// <summary>Previous camera path FormID in chain (pPrevPath pointer at +68).</summary>
    public uint PreviousPathFormId { get; init; }

    /// <summary>List of camera shot FormIDs (SNAM subrecords on ESM side).</summary>
    public List<uint> CameraShotFormIds { get; init; } = [];

    /// <summary>Number of CTDA conditions in the ESM-side record.</summary>
    public int ConditionCount { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

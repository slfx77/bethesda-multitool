using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

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

    /// <summary>Number of CTDA conditions in the source record (parser captures this even when
    /// <see cref="Conditions" /> is empty, e.g. legacy ESM scans that ran before 4.2d).</summary>
    public int ConditionCount { get; init; }

    /// <summary>
    ///     CTDA conditions gating when the path is eligible. Populated by the ESM parser
    ///     and the runtime reader (walking the <c>TESCondition</c> BSSimpleList at
    ///     <c>BGSCameraPath+40</c>). Empty when neither path captured conditions.
    /// </summary>
    public List<DialogueCondition> Conditions { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

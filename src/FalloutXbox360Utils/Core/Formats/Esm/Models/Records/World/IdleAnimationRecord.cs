namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Idle Animation (IDLE) record. Defines a KF animation file plus its
///     trigger conditions and loop behavior. PDB struct: TESIdleForm
///     (92 bytes, FormType 0x48).
/// </summary>
public record IdleAnimationRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Animation file path (cModel BSStringT at +44).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Parent idle FormID (ANAM subrecord, ESM-side only).</summary>
    public uint ParentIdleFormId { get; init; }

    /// <summary>Previous idle FormID in a chain (ANAM, ESM-side only).</summary>
    public uint PreviousIdleFormId { get; init; }

    /// <summary>Animation data byte (IDLE_DATA at +72, byte 0).</summary>
    public byte AnimData { get; init; }

    /// <summary>Minimum loop count.</summary>
    public byte LoopMin { get; init; }

    /// <summary>Maximum loop count.</summary>
    public byte LoopMax { get; init; }

    /// <summary>Replay delay (uint16, big-endian on Xbox).</summary>
    public ushort ReplayDelay { get; init; }

    /// <summary>Extra flags byte (FNV-only extension, may be missing on PC).</summary>
    public byte FlagsEx { get; init; }

    /// <summary>Number of CTDA conditions in the ESM-side record.</summary>
    public int ConditionCount { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}

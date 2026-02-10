namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Quest from memory dump.
///     Aggregates data from QUST main record header, stages, objectives, etc.
/// </summary>
public record QuestRecord
{
    /// <summary>FormID of the quest record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "vDialogueGoodsprings").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (e.g., "Ain't That a Kick in the Head").</summary>
    public string? FullName { get; init; }

    /// <summary>Quest flags from DATA subrecord.</summary>
    public byte Flags { get; init; }

    /// <summary>Quest priority from DATA subrecord.</summary>
    public byte Priority { get; init; }

    /// <summary>Quest delay in game hours between repeated stages (DATA bytes 4-7).</summary>
    public float QuestDelay { get; init; }

    /// <summary>Quest script FormID (SCRI subrecord).</summary>
    public uint? Script { get; init; }

    /// <summary>Quest stages (INDX + log entries).</summary>
    public List<QuestStage> Stages { get; init; } = [];

    /// <summary>Quest objectives (QOBJ + display text).</summary>
    public List<QuestObjective> Objectives { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

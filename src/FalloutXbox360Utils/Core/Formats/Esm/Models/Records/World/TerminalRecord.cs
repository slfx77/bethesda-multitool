namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Parsed Terminal record.
/// </summary>
public record TerminalRecord
{
    /// <summary>FormID of the terminal record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Object bounds (OBND subrecord). Null when absent.</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Model path (MODL subrecord). Null when absent.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Terminal header text (DESC subrecord).</summary>
    public string? HeaderText { get; init; }

    /// <summary>Script FormID (SCRI subrecord). Null when absent.</summary>
    public uint? ScriptFormId { get; init; }

    /// <summary>Sound-loop FormID (SNAM subrecord, BGSSoundForm). Null when absent.</summary>
    public uint? SoundLoopFormId { get; init; }

    /// <summary>Password-note FormID (PNAM subrecord, BGSNote). Null when absent.</summary>
    public uint? PasswordNoteFormId { get; init; }

    /// <summary>Terminal difficulty (0-4).</summary>
    public byte Difficulty { get; init; }

    /// <summary>Terminal flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Terminal server type byte from DNAM.</summary>
    public byte ServerType { get; init; }

    /// <summary>Terminal menu items.</summary>
    public List<TerminalMenuItem> MenuItems { get; init; } = [];

    /// <summary>Password text (if resolvable). Currently always null on runtime reads —
    /// the BGSTerminal struct doesn't store password text inline, and pPassword
    /// pointer-chase recovery is blocked by a PDB-vs-runtime layout discrepancy
    /// (see RuntimeQuestTerminalReader and the plan file's Tier 3.2 notes).</summary>
    public string? Password { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable difficulty name.</summary>
    public string DifficultyName => Difficulty switch
    {
        0 => "Very Easy",
        1 => "Easy",
        2 => "Average",
        3 => "Hard",
        4 => "Very Hard",
        _ => $"Unknown ({Difficulty})"
    };
}

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Terminal from memory dump.
/// </summary>
public record TerminalRecord
{
    /// <summary>FormID of the terminal record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Terminal header text (DESC subrecord).</summary>
    public string? HeaderText { get; init; }

    /// <summary>Terminal difficulty (0-4).</summary>
    public byte Difficulty { get; init; }

    /// <summary>Terminal flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Terminal menu items.</summary>
    public List<TerminalMenuItem> MenuItems { get; init; } = [];

    /// <summary>Password (if set).</summary>
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

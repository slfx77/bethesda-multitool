namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Book from memory dump.
///     Contains text content similar to notes.
/// </summary>
public record ReconstructedBook
{
    /// <summary>FormID of the book record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Book text content (DESC subrecord).</summary>
    public string? Text { get; init; }

    /// <summary>Value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weight.</summary>
    public float Weight { get; init; }

    /// <summary>Book flags (teaches skill, etc.).</summary>
    public byte Flags { get; init; }

    /// <summary>Skill taught by this book (if any).</summary>
    public byte SkillTaught { get; init; }

    /// <summary>Model path.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Whether this book teaches a skill.</summary>
    public bool TeachesSkill => (Flags & 0x01) != 0;
}

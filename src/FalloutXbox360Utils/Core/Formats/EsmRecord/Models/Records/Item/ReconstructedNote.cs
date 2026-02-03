namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Note with text content.
///     Aggregates data from NOTE main record header, EDID, FULL, and text content.
/// </summary>
public record ReconstructedNote
{
    /// <summary>FormID of the NOTE record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Note type (0=Sound, 1=Text, 2=Image, 3=Voice).</summary>
    public byte NoteType { get; init; }

    /// <summary>Text content (TNAM subrecord, or DESC for books).</summary>
    public string? Text { get; init; }

    /// <summary>Model file path (TESModel.cModel).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable note type.</summary>
    public string NoteTypeName => NoteType switch
    {
        0 => "Sound",
        1 => "Text",
        2 => "Image",
        3 => "Voice",
        _ => $"Unknown ({NoteType})"
    };
}

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed dialogue response from INFO record.
///     Aggregates data from INFO main record header, NAM1 (response text), TRDT (emotion), etc.
/// </summary>
public record ReconstructedDialogue
{
    /// <summary>FormID of the INFO record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID of the INFO record.</summary>
    public string? EditorId { get; init; }

    /// <summary>Parent DIAL topic FormID.</summary>
    public uint? TopicFormId { get; init; }

    /// <summary>Parent quest FormID (QSTI subrecord).</summary>
    public uint? QuestFormId { get; init; }

    /// <summary>Speaker NPC FormID (if specified).</summary>
    public uint? SpeakerFormId { get; init; }

    /// <summary>Response entries (each INFO can have multiple responses).</summary>
    public List<DialogueResponse> Responses { get; init; } = [];

    /// <summary>Previous INFO FormID (link chain).</summary>
    public uint? PreviousInfo { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}

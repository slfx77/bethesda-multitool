namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Dialog Topic from memory dump.
/// </summary>
public record ReconstructedDialogTopic
{
    /// <summary>FormID of the dialog topic record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Parent quest FormID.</summary>
    public uint? QuestFormId { get; init; }

    /// <summary>Topic type (0=Topic, 1=Conversation, 2=Combat, etc.).</summary>
    public byte TopicType { get; init; }

    /// <summary>Topic flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Number of INFO responses under this topic.</summary>
    public int ResponseCount { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable topic type name.</summary>
    public string TopicTypeName => TopicType switch
    {
        0 => "Topic",
        1 => "Conversation",
        2 => "Combat",
        3 => "Persuasion",
        4 => "Detection",
        5 => "Service",
        6 => "Miscellaneous",
        7 => "Radio",
        _ => $"Unknown ({TopicType})"
    };
}

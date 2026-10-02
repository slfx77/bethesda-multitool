namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     Parsed Message (MESG) record.
///     In-game popup messages, tutorials, and notifications.
/// </summary>
public record MessageRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? Description { get; init; }

    public MessageFieldSource DescriptionSource { get; init; } = MessageFieldSource.StoredRecord;
    public MessageFieldSource ButtonSource { get; init; } = MessageFieldSource.StoredRecord;

    /// <summary>Parsed accessor button count, including a positively parsed empty list.</summary>
    public int? StoredButtonCount { get; init; }

    /// <summary>The complete runtime button representation, separate from accessor-selected values.</summary>
    public RuntimeMessageButtons? RuntimeButtons { get; init; }

    /// <summary>Additional physical runtime occurrences of the same message identity.</summary>
    public List<RuntimeMessageButtons> AdditionalRuntimeButtons { get; init; } = [];

    /// <summary>Runtime-only extraction status and offsets; null for ordinary plugin records.</summary>
    public RuntimeMessageEvidence? RuntimeEvidence { get; init; }

    /// <summary>Icon path from ICON subrecord.</summary>
    public string? Icon { get; init; }

    /// <summary>Associated quest FormID from QNAM subrecord.</summary>
    public uint QuestFormId { get; init; }

    /// <summary>Message flags from DNAM: 1=MessageBox, 2=AutoDisplay.</summary>
    public uint Flags { get; init; }

    /// <summary>Display time from TNAM subrecord.</summary>
    public uint DisplayTime { get; init; }

    /// <summary>Button text entries from ITXT subrecords.</summary>
    public List<string> Buttons { get; init; } = [];

    /// <summary>
    ///     Conditions following each ITXT, indexed in the same zero-based order as <see cref="Buttons" />.
    ///     Empty button text still occupies an index. Older callers may omit trailing empty lists.
    /// </summary>
    public List<List<DialogueCondition>> ButtonConditions { get; init; } = [];

    /// <summary>CTDA entries before the first ITXT; their button ownership is unknown.</summary>
    public List<DialogueCondition> UnassignedConditions { get; init; } = [];

    public IReadOnlyList<DialogueCondition> GetButtonConditions(int index) =>
        index >= 0 && index < ButtonConditions.Count ? ButtonConditions[index] : [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    public bool IsMessageBox => (Flags & 1) != 0;
    public bool IsAutoDisplay => (Flags & 2) != 0;
}

public enum MessageFieldSource
{
    StoredRecord,
    RuntimeObject,
    RuntimeMappedRecord,
    Unavailable
}

/// <summary>Text and conditions from one runtime object; never joined to stored buttons by index.</summary>
public sealed record RuntimeMessageButtons
{
    public long ObjectFileOffset { get; init; }
    public RuntimeMessageEvidence? Evidence { get; init; }
    public List<string> Buttons { get; init; } = [];
    public List<List<DialogueCondition>> ButtonConditions { get; init; } = [];

    public IReadOnlyList<DialogueCondition> GetConditions(int index) =>
        index >= 0 && index < ButtonConditions.Count ? ButtonConditions[index] : [];
}

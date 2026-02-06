namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Data extracted from a runtime TESTopic C++ struct in the crash dump.
///     Intermediate record used during dialogue topic reconstruction before merging with ESM data.
/// </summary>
public record RuntimeDialogTopicInfo
{
    /// <summary>FormID from the runtime struct.</summary>
    public uint FormId { get; init; }

    /// <summary>Topic type (0=Topic, 1=Conversation, 2=Combat, etc.).</summary>
    public byte TopicType { get; init; }

    /// <summary>Topic flags (bit0=Rumors, bit1=TopLevel).</summary>
    public byte Flags { get; init; }

    /// <summary>Topic ordering priority (runtime m_fPriority).</summary>
    public float Priority { get; init; }

    /// <summary>Number of INFO responses under this topic (runtime m_uiTopicCount).</summary>
    public uint TopicCount { get; init; }

    /// <summary>Display name from TESFullName BSStringT.</summary>
    public string? FullName { get; init; }

    /// <summary>Fallback prompt text (runtime cDummyPrompt BSStringT).</summary>
    public string? DummyPrompt { get; init; }
}

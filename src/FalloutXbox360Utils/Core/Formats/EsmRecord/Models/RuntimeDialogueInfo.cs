namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Data extracted from a runtime TESTopicInfo C++ struct in the crash dump.
///     Intermediate record used during dialogue reconstruction before merging with ESM data.
/// </summary>
public record RuntimeDialogueInfo
{
    /// <summary>FormID from the runtime struct (validated against hash table).</summary>
    public uint FormId { get; init; }

    /// <summary>Ordering index within the parent topic.</summary>
    public ushort InfoIndex { get; init; }

    /// <summary>Topic type from TOPIC_INFO_DATA.type.</summary>
    public byte TopicType { get; init; }

    /// <summary>Next speaker enum from TOPIC_INFO_DATA.nextSpeaker.</summary>
    public byte NextSpeaker { get; init; }

    /// <summary>Info flags: Goodbye(0x01), Random(0x02), RandomEnd(0x04), SayOnce(0x10), SpeechChallenge(0x80).</summary>
    public byte InfoFlags { get; init; }

    /// <summary>Extended info flags: SayOnceADay(0x01), AlwaysDarkened(0x02).</summary>
    public byte InfoFlagsExt { get; init; }

    /// <summary>Speaker NPC FormID (from pSpeaker pointer).</summary>
    public uint? SpeakerFormId { get; init; }

    /// <summary>Speech challenge difficulty (0=None, 1=VeryEasy, ..., 5=VeryHard).</summary>
    public uint Difficulty { get; init; }

    /// <summary>Parent quest FormID (from pOwnerQuest pointer).</summary>
    public uint? QuestFormId { get; init; }

    /// <summary>Player-visible prompt text (from cPrompt BSStringT).</summary>
    public string? PromptText { get; init; }

    /// <summary>File offset in the dump where the TESTopicInfo struct was read.</summary>
    public long DumpOffset { get; init; }
}

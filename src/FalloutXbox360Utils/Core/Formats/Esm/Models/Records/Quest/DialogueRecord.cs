namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed dialogue response from INFO record.
///     Aggregates data from INFO main record header, NAM1 (response text), TRDT (emotion),
///     runtime TESTopicInfo struct (speaker, quest, flags), and linking subrecords (TCLT/TCLF/NAME).
/// </summary>
public record DialogueRecord
{
    /// <summary>FormID of the INFO record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID of the INFO record.</summary>
    public string? EditorId { get; init; }

    /// <summary>Parent DIAL topic FormID.</summary>
    public uint? TopicFormId { get; init; }

    /// <summary>Parent quest FormID (QSTI subrecord or runtime pOwnerQuest).</summary>
    public uint? QuestFormId { get; init; }

    /// <summary>Speaker NPC FormID (from ANAM subrecord or CTDA GetIsID condition).</summary>
    public uint? SpeakerFormId { get; init; }

    /// <summary>Speaker faction FormID (from CTDA GetInFaction condition — shared/generic dialogue).</summary>
    public uint? SpeakerFactionFormId { get; init; }

    /// <summary>Speaker race FormID (from CTDA GetIsRace condition — creature/race dialogue).</summary>
    public uint? SpeakerRaceFormId { get; init; }

    /// <summary>Speaker voice type FormID (from CTDA GetIsVoiceType condition — generic dialogue).</summary>
    public uint? SpeakerVoiceTypeFormId { get; init; }

    /// <summary>All CTDA condition function indices found on this INFO (for diagnostics).</summary>
    public List<ushort> ConditionFunctions { get; init; } = [];

    /// <summary>Response entries (each INFO can have multiple responses).</summary>
    public List<DialogueResponse> Responses { get; init; } = [];

    /// <summary>Previous INFO FormID (PNAM link chain).</summary>
    public uint? PreviousInfo { get; init; }

    // Runtime TESTopicInfo fields (from crash dump C++ struct)

    /// <summary>Player-visible prompt text (runtime cPrompt BSStringT).</summary>
    public string? PromptText { get; init; }

    /// <summary>Ordering index within the parent topic (runtime iInfoIndex).</summary>
    public ushort InfoIndex { get; init; }

    /// <summary>Info flags byte: Goodbye(0x01), Random(0x02), RandomEnd(0x04), SayOnce(0x10), SpeechChallenge(0x80).</summary>
    public byte InfoFlags { get; init; }

    /// <summary>Extended info flags: SayOnceADay(0x01), AlwaysDarkened(0x02).</summary>
    public byte InfoFlagsExt { get; init; }

    /// <summary>Speech challenge difficulty (0=None, 1=VeryEasy, ..., 5=VeryHard).</summary>
    public uint Difficulty { get; init; }

    // ESM linking subrecords

    /// <summary>Topics this INFO links TO (TCLT subrecords — choosing this response leads to these topics).</summary>
    public List<uint> LinkToTopics { get; init; } = [];

    /// <summary>Topics this INFO links FROM (TCLF subrecords — these topics can lead here).</summary>
    public List<uint> LinkFromTopics { get; init; } = [];

    /// <summary>Topics unlocked by saying this INFO (NAME subrecords).</summary>
    public List<uint> AddTopics { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    // Computed properties

    /// <summary>Whether this INFO ends the conversation.</summary>
    public bool IsGoodbye => (InfoFlags & 0x01) != 0;

    /// <summary>Whether this INFO can only be said once.</summary>
    public bool IsSayOnce => (InfoFlags & 0x10) != 0;

    /// <summary>Whether this INFO is a speech challenge option.</summary>
    public bool IsSpeechChallenge => (InfoFlags & 0x80) != 0;

    /// <summary>Human-readable difficulty name for speech challenges.</summary>
    public string DifficultyName => Difficulty switch
    {
        0 => "None",
        1 => "Very Easy",
        2 => "Easy",
        3 => "Average",
        4 => "Hard",
        5 => "Very Hard",
        _ => $"Unknown ({Difficulty})"
    };
}

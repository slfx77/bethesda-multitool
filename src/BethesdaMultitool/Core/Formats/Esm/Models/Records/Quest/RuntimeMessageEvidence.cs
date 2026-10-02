namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>Captured runtime evidence; missing bytes are not an empty authored menu.</summary>
public sealed record RuntimeMessageEvidence(
    uint? DescriptionFileOffset,
    string DescriptionStatus,
    string ButtonListStatus,
    IReadOnlyList<RuntimeMessageButtonEvidence> Buttons)
{
    public IReadOnlyList<RuntimeMessageDescriptionMapping> DescriptionMappings { get; init; } = [];
}

/// <summary>A checked same-capture source-file mapping; text is retained even when mappings disagree.</summary>
public sealed record RuntimeMessageDescriptionMapping(
    long SegmentBaseVirtualAddress, int CalibrationMatches, uint CalibrationExampleFormId,
    long RecordVirtualAddress, long? RecordDumpOffset, string Status,
    long? DescriptionVirtualAddress = null, long? DescriptionDumpOffset = null, string? Text = null);

public sealed record RuntimeMessageButtonEvidence(
    int Index, uint ItemVirtualAddress, long? ItemFileOffset,
    uint? TextVirtualAddress, long? TextFileOffset, string TextStatus,
    string ConditionStatus);

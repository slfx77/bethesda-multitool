namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal sealed record DialogueTesFileMappingSegment
{
    public long BaseVirtualAddress { get; init; }
    public uint MinTesFileOffset { get; init; }
    public uint MaxTesFileOffset { get; init; }
    public int MatchCount { get; init; }
    public uint ExampleFormId { get; init; }
    public long ExampleRawRecordOffset { get; init; }

    /// <summary>True if the given TES-file offset falls within this segment's min/max range.</summary>
    public bool Contains(uint tesFileOffset)
    {
        return tesFileOffset >= MinTesFileOffset && tesFileOffset <= MaxTesFileOffset;
    }
}

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed record DialogueTesFileMappingSegment
{
    public long BaseVirtualAddress { get; init; }
    public uint MinTesFileOffset { get; init; }
    public uint MaxTesFileOffset { get; init; }
    public int MatchCount { get; init; }
    public uint ExampleFormId { get; init; }
    public long ExampleRawRecordOffset { get; init; }

    public bool Contains(uint tesFileOffset)
    {
        return tesFileOffset >= MinTesFileOffset && tesFileOffset <= MaxTesFileOffset;
    }
}
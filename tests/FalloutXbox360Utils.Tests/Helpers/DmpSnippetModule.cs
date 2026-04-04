namespace FalloutXbox360Utils.Tests.Helpers;

internal sealed class DmpSnippetModule
{
    public required string Name { get; init; }
    public long BaseAddress { get; init; }
    public int Size { get; init; }
    public uint Checksum { get; init; }
    public uint TimeDateStamp { get; init; }
}
namespace FalloutXbox360Utils.Tests.Helpers;

internal sealed class DmpSnippetMemoryRegion
{
    public long VirtualAddress { get; init; }
    public long Size { get; init; }
    public long FileOffset { get; init; }
}
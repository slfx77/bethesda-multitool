namespace FalloutXbox360Utils.Core;

public sealed class PdbGlobal
{
    public required string Kind { get; init; }
    public required int Section { get; init; }
    public required uint Offset { get; init; }
    public required string Name { get; init; }
}

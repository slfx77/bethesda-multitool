using BethesdaMultitool.Core.RuntimeBuffer;

namespace BethesdaMultitool.Core.Pdb;

public sealed class ResolvedGlobal
{
    public required PdbGlobal Global { get; init; }
    public long VirtualAddress { get; init; }
    public long FileOffset { get; init; }
    public uint PointerValue { get; init; }
    public PointerClassification Classification { get; init; }
    public string? StructureInfo { get; set; }
}

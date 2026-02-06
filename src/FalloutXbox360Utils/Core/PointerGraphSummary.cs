namespace FalloutXbox360Utils.Core;

public sealed class PointerGraphSummary
{
    public int TotalPointerDenseGaps { get; set; }
    public long TotalPointerDenseBytes { get; set; }
    public int ObjectArrayGaps { get; set; }
    public int HashTableGaps { get; set; }
    public int LinkedListGaps { get; set; }
    public int MixedStructureGaps { get; set; }
    public int TotalVtablePointersFound { get; set; }
    public Dictionary<uint, int> TopVtableAddresses { get; } = [];
}

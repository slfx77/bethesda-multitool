namespace FalloutXbox360Utils.Core.Pdb;

public sealed class PdbAnalysisResult
{
    public int TotalParsed { get; set; }
    public int DataSectionGlobals { get; set; }
    public int UnresolvableCount { get; set; }
    public int NullCount { get; set; }
    public int UnmappedCount { get; set; }
    public int ModuleRangeCount { get; set; }
    public int HeapCount { get; set; }

    public List<ResolvedGlobal> ResolvedGlobals { get; } = [];
    public List<ResolvedGlobal> InterestingGlobals { get; } = [];

    /// <summary>
    ///     Remove duplicate entries from InterestingGlobals (same name + pointer value).
    /// </summary>
    public void DeduplicateInterestingGlobals()
    {
        var seen = new HashSet<(string, uint)>();
        InterestingGlobals.RemoveAll(g => !seen.Add((g.Global.Name, g.PointerValue)));
    }
}

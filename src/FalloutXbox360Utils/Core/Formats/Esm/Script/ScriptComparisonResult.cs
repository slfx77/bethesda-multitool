namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Result of comparing SCTX source vs decompiled script text.
/// </summary>
public sealed class ScriptComparisonResult
{
    public int MatchCount { get; set; }
    public Dictionary<string, int> MismatchesByCategory { get; } = new();

    /// <summary>
    ///     Differences that are semantically correct but worth tracking for diagnostics.
    ///     DroppedParameter: compiler strips trailing params the decompiler correctly omits.
    ///     NumberFormat: IEEE 754 representation prevents exact float formatting recovery.
    ///     These count toward MatchCount, not TotalMismatches.
    /// </summary>
    public Dictionary<string, int> ToleratedDifferences { get; } = new();

    public List<(string Source, string Decompiled, string Category)> Examples { get; } = [];

    public int TotalMismatches => MismatchesByCategory.Values.Sum();
    public int TotalTolerated => ToleratedDifferences.Values.Sum();
    public int TotalLines => MatchCount + TotalMismatches;

    public double MatchRate => TotalLines > 0 ? 100.0 * MatchCount / TotalLines : 0;
}
namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>The DDX gate's verdict: pass, or every failing check.</summary>
/// <param name="Passed">True when every check held.</param>
/// <param name="Failures">One sentence per failed check, in check order (empty on a pass).</param>
internal sealed record NifDdxGateResult(bool Passed, IReadOnlyList<string> Failures)
{
    /// <summary>The failures joined into one reason, or null on a pass.</summary>
    public string? Reason => Failures.Count == 0 ? null : string.Join("; ", Failures);
}

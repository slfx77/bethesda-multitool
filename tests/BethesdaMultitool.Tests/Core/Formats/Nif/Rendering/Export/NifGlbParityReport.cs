using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>The measured result of one <see cref="NifGlbParityOracle" /> comparison, including every failure.</summary>
/// <remarks>
///     The oracle records instead of throwing so a corpus gate can keep measuring after the first difference and
///     write its receipt. <see cref="NifGlbParityOracle.AssertEquivalent" /> turns a failed report into a test
///     failure for callers that want one.
/// </remarks>
internal sealed class NifGlbParityReport
{
    /// <summary>How many failure messages are kept; every failure is still counted.</summary>
    internal const int RecordedFailureLimit = 64;

    private readonly List<string> _failures = [];

    /// <summary>The first <see cref="RecordedFailureLimit" /> failure messages.</summary>
    internal IReadOnlyList<string> Failures => _failures;

    /// <summary>Every failure found, including those beyond the recorded limit.</summary>
    internal int FailureCount { get; private set; }

    /// <summary>Whether the pair is equivalent under every rule.</summary>
    internal bool Passed => FailureCount == 0;

    /// <summary>Drawn triangle occurrences decoded from the native GLB.</summary>
    internal int NativeTriangles { get; set; }

    /// <summary>Drawn triangle occurrences decoded from the normalized GLB.</summary>
    internal int SharedTriangles { get; set; }

    /// <summary>Normalized triangles removed as exactly repeated-position triangles before pairing.</summary>
    internal int RemovedSharedTriangles { get; set; }

    /// <summary>Pairs found by the exact bit-level key.</summary>
    internal int ExactPairs { get; set; }

    /// <summary>Pairs found by tolerance matching.</summary>
    internal int TolerancePairs { get; set; }

    /// <summary>Native triangles left without a normalized counterpart.</summary>
    internal int UnmatchedNativeTriangles { get; set; }

    /// <summary>Retained normalized triangles left without a native counterpart.</summary>
    internal int UnmatchedSharedTriangles { get; set; }

    /// <summary>Normalized primitive occurrences whose native fallback tangents were proven inert and ignored.</summary>
    internal int IgnoredFallbackTangentPrimitives { get; set; }

    /// <summary>Material signatures for which the normalized GLB has more rows than the native one.</summary>
    internal int MergedMaterialRowSignatures { get; set; }

    /// <summary>Normalized material signatures referenced only by removed repeated-position triangles.</summary>
    internal int RemovedOnlyMaterialSignatures { get; set; }

    /// <summary>Native COLOR_0 accessor types, counted per drawn primitive occurrence.</summary>
    internal SortedDictionary<string, int> NativeColorAccessors { get; } = new(StringComparer.Ordinal);

    /// <summary>Normalized COLOR_0 accessor types, counted per drawn primitive occurrence.</summary>
    internal SortedDictionary<string, int> SharedColorAccessors { get; } = new(StringComparer.Ordinal);

    /// <summary>Paired triangles whose tangents diverge, by the normalized primitive's tangent class.</summary>
    internal SortedDictionary<string, int> TangentDivergences { get; } = new(StringComparer.Ordinal);

    /// <summary>Compared normalized primitive occurrences per feature class.</summary>
    internal SortedDictionary<string, int> FeatureClasses { get; } = new(StringComparer.Ordinal);

    /// <summary>Records one failure; messages beyond the limit are counted but not kept.</summary>
    /// <param name="message">What differs, in terms a reader can act on.</param>
    internal void Fail(string message)
    {
        FailureCount++;
        if (_failures.Count < RecordedFailureLimit)
        {
            _failures.Add(message);
        }
    }

    /// <summary>Adds one to a counter in one of this report's tallies.</summary>
    /// <param name="tally">The tally to change.</param>
    /// <param name="key">The counter name.</param>
    internal static void Increment(SortedDictionary<string, int> tally, string key)
    {
        tally[key] = tally.GetValueOrDefault(key) + 1;
    }

    /// <summary>A one-paragraph account of the comparison and its first failures.</summary>
    /// <returns>Text suitable for a test failure message.</returns>
    internal string Summary()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
                $"{FailureCount} failure(s); native {NativeTriangles} triangles, normalized {SharedTriangles} ")
            .Append(CultureInfo.InvariantCulture,
                $"({RemovedSharedTriangles} removed as repeated positions); {ExactPairs} exact and ")
            .Append(CultureInfo.InvariantCulture,
                $"{TolerancePairs} tolerance pairs; unmatched native {UnmatchedNativeTriangles}, ")
            .Append(CultureInfo.InvariantCulture, $"unmatched normalized {UnmatchedSharedTriangles}.");
        foreach (var failure in _failures)
        {
            text.Append(Environment.NewLine).Append("  ").Append(failure);
        }

        if (FailureCount > _failures.Count)
        {
            text.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture,
                $"  ... and {FailureCount - _failures.Count} more.");
        }

        return text.ToString();
    }
}

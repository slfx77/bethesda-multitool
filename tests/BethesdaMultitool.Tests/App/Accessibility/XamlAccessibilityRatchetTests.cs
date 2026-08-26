using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App.Accessibility;

/// <summary>
///     Ratchet test: tracks the set of interactive XAML controls currently missing an
///     accessible name. Assert that <em>no regressions</em> are introduced — the scan's
///     current output must be a subset of the recorded baseline.
///     Workflow when adding controls:
///     <list type="number">
///         <item>Give the new control <c>AutomationProperties.Name</c> / <c>LabeledBy</c> / <c>x:Uid</c>.</item>
///         <item>
///             Run this test — if it fails, either add the missing accessibility metadata or
///             (as a last resort) add the control's entry to <c>a11y-baseline.txt</c>.
///         </item>
///     </list>
///     Workflow when fixing existing gaps: remove the control's entry from the baseline when
///     its accessibility metadata lands.
/// </summary>
public sealed class XamlAccessibilityRatchetTests
{
    private static string AppDirectory =>
        Path.Combine(SourceContract.RepoRoot, "src", "BethesdaMultitool", "App");

    private static string BaselinePath =>
        Path.Combine(SourceContract.RepoRoot, "tests", "BethesdaMultitool.Tests",
            "App", "Accessibility", "a11y-baseline.txt");

    [Fact]
    public void InteractiveControls_Have_AccessibleNames_OrAreListedInBaseline()
    {
        var gaps = XamlAccessibilityScanner.Scan(AppDirectory);

        // Baseline is a plain-text file — one "file:controlType:localIdentifier" per line
        // (identifier may be empty). Compared set-wise so line order / additions don't matter.
        var baseline = File.Exists(BaselinePath)
            ? File.ReadAllLines(BaselinePath)
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
                .Select(line => line.Trim())
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var current = gaps.Select(ToKey).ToHashSet(StringComparer.Ordinal);

        var regressions = current.Except(baseline, StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        var fixed_ = baseline.Except(current, StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (regressions.Count > 0)
        {
            var gapByKey = gaps.ToDictionary(ToKey, g => g);
            var regressionsWithLines = regressions.Select(r =>
                gapByKey.TryGetValue(r, out var g)
                    ? $"{r}  (line {g.LineNumber})"
                    : r);
            var header = $"{regressions.Count} new accessibility regression(s). " +
                         "Add AutomationProperties.Name / LabeledBy / x:Uid to these controls, " +
                         "or (last resort) append to a11y-baseline.txt:\n";
            Assert.Fail(header + string.Join("\n", regressionsWithLines));
        }

        // Fixed-but-still-in-baseline entries are *not* a failure — this keeps the test
        // quiet during incremental fix waves. The follow-up commit should trim the baseline,
        // but leaving a stale entry doesn't regress behavior.
        //
        // For visibility when running locally, print them:
        if (fixed_.Count > 0)
        {
            Console.WriteLine(
                $"[accessibility] {fixed_.Count} baseline entries are no longer failing — " +
                "consider trimming tests/BethesdaMultitool.Tests/App/Accessibility/a11y-baseline.txt");
            foreach (var item in fixed_)
                Console.WriteLine("  - " + item);
        }
    }

    [Fact]
    public void Baseline_IsSorted_AndHas_NoDuplicates()
    {
        Assert.SkipUnless(File.Exists(BaselinePath), $"Accessibility baseline not present at {BaselinePath}.");

        var entries = File.ReadAllLines(BaselinePath)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Trim())
            .ToList();

        var sorted = entries.OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, entries);

        var duplicates = entries.GroupBy(s => s)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicates);
    }

    private static string ToKey(XamlAccessibilityScanner.Gap gap)
    {
        return $"{gap.File}:{gap.ControlType}:{gap.LocalIdentifier ?? ""}";
    }

    /// <summary>
    ///     Generator, not a test — it asserts nothing. Emits the scanner's current findings so a
    ///     fresh run can be captured as the baseline. Tagged <c>Category=Tool</c> so correctness
    ///     sweeps can filter it out, and it writes to the gitignored <c>TestOutput/</c> rather
    ///     than into the source tree: a test run must not mutate tracked files.
    ///     Run via <c>--filter-method '*DumpCurrentGaps*'</c>.
    /// </summary>
    [Fact(Skip = "Generator only — run explicitly to regenerate a11y-baseline.txt")]
    [Trait("Category", TestCategories.Tool)]
    public void DumpCurrentGaps()
    {
        var gaps = XamlAccessibilityScanner.Scan(AppDirectory);
        var lines = gaps.Select(g => $"{ToKey(g)}  (line {g.LineNumber})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"# total: {gaps.Count}");

        // Save to the gitignored TestOutput/ so callers can inspect without parsing stdout.
        var dumpDirectory = Path.Combine(SourceContract.RepoRoot, "TestOutput");
        Directory.CreateDirectory(dumpDirectory);
        var dumpPath = Path.Combine(dumpDirectory, "a11y-scan-latest.txt");
        File.WriteAllLines(dumpPath,
            lines.Prepend("# Regenerated by XamlAccessibilityRatchetTests.DumpCurrentGaps"));
        Console.WriteLine($"# written: {dumpPath}");
    }
}
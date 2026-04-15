using FalloutXbox360Utils.Tests.App.Accessibility;
using Xunit;

namespace FalloutXbox360Utils.Tests.App.Accessibility;

/// <summary>
///     Ratchet test: tracks the set of interactive XAML controls currently missing an
///     accessible name. Assert that <em>no regressions</em> are introduced — the scan's
///     current output must be a subset of the recorded baseline.
///
///     Workflow when adding controls:
///     <list type="number">
///         <item>Give the new control <c>AutomationProperties.Name</c> / <c>LabeledBy</c> / <c>x:Uid</c>.</item>
///         <item>Run this test — if it fails, either add the missing accessibility metadata or
///             (as a last resort) add the control's entry to <c>a11y-baseline.txt</c>.</item>
///     </list>
///
///     Workflow when fixing existing gaps: remove the control's entry from the baseline when
///     its accessibility metadata lands.
/// </summary>
public sealed class XamlAccessibilityRatchetTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static string AppDirectory =>
        Path.Combine(FindRepoRoot(), "src", "FalloutXbox360Utils", "App");

    private static string BaselinePath =>
        Path.Combine(FindRepoRoot(), "tests", "FalloutXbox360Utils.Tests",
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
            var header = $"{regressions.Count} new accessibility regression(s). " +
                         "Add AutomationProperties.Name / LabeledBy / x:Uid to these controls, " +
                         "or (last resort) append to a11y-baseline.txt:\n";
            Assert.Fail(header + string.Join("\n", regressions));
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
                "consider trimming tests/FalloutXbox360Utils.Tests/App/Accessibility/a11y-baseline.txt");
            foreach (var item in fixed_)
                Console.WriteLine("  - " + item);
        }
    }

    [Fact]
    public void Baseline_IsSorted_AndHas_NoDuplicates()
    {
        if (!File.Exists(BaselinePath))
            return;

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
    ///     Diagnostic fact — not a test assertion. Emits the scanner's current findings
    ///     to the console so a fresh run can be captured as the baseline. Run via
    ///     <c>dotnet test --filter DumpCurrentGaps</c>.
    /// </summary>
    [Fact(Skip = "Diagnostic only — uncomment to regenerate a11y-baseline.txt")]
    public void DumpCurrentGaps()
    {
        var gaps = XamlAccessibilityScanner.Scan(AppDirectory);
        var lines = gaps.Select(ToKey).OrderBy(s => s, StringComparer.Ordinal);
        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"# total: {gaps.Count}");

        // Also save to disk so callers can inspect without parsing stdout.
        var dumpPath = Path.Combine(Path.GetDirectoryName(BaselinePath)!, "a11y-scan-latest.txt");
        File.WriteAllLines(dumpPath,
            lines.Prepend("# Regenerated by XamlAccessibilityRatchetTests.DumpCurrentGaps"));
    }
}

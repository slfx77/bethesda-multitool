using System.Text.RegularExpressions;
using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Keeps every opt-in execution guard paired with its <c>[Trait("Category", …)]</c>.
///     <para>
///         The guard is what actually skips a test; the trait is what <c>--filter-trait</c>
///         selects. When they disagree the failure is silent in the worst direction: a targeted
///         run like <c>--filter-trait Category=BucketB</c> quietly selects a subset and reports
///         success having exercised only part of what it claimed. Measured 2026-08-21 before this
///         gate existed, only 26 of 95 Bucket-B files carried the trait, and
///         <c>ShaderCompileTestGuard</c> had none at all.
///     </para>
///     <para>
///         One source pass checks even guard calls not reached by today's data. Each guard also
///         checks xUnit's effective traits at runtime, so a sibling method's attribute cannot
///         incorrectly satisfy the contract for the executing test.
///     </para>
/// </summary>
public class TestCategoryConsistencyTests
{
    private static readonly (string Guard, string Category)[] GuardCategories =
    [
        (nameof(BucketBTestGuard), BucketBTestGuard.Category),
        (nameof(GpuTestGuard), GpuTestGuard.Category),
        (nameof(ShaderCompileTestGuard), ShaderCompileTestGuard.Category)
    ];

    [Fact]
    public void EveryFileCallingAGuard_AlsoCarriesItsMatchingCategoryTrait()
    {
        var testRoot = Path.Combine(SourceContract.RepoRoot, "tests", "BethesdaMultitool.Tests");
        var contracts = GuardCategories.Select(pair => (pair.Guard, pair.Category, Pattern: new Regex(
            @"\[\s*Trait\(\s*""Category""\s*,\s*(?:TestCategories\." + Regex.Escape(pair.Category) + "|"
            + Regex.Escape(pair.Guard) + @"\.Category|""" + Regex.Escape(pair.Category) + @""")\s*\)\s*\]",
            RegexOptions.CultureInvariant))).ToArray();

        foreach (var (guard, category, pattern) in contracts)
        {
            Assert.Matches(pattern, $"[Trait(\"Category\", TestCategories.{category})]");
            Assert.Matches(pattern, $"[Trait(\"Category\", {guard}.Category)]");
            Assert.DoesNotMatch(pattern, "[Trait(\"Category\", TestCategories.Benchmark)]");
            // This test has no opt-in trait. Metadata must reject a guard even if another
            // method or a textual declaration elsewhere in this file names its category.
            Assert.Throws<Xunit.Sdk.TrueException>(() => TestCategories.RequireCurrent(category));
        }

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || Path.GetFileName(file) == $"{nameof(TestCategoryConsistencyTests)}.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var (guard, category, pattern) in contracts)
            {
                if (text.Contains($"{guard}.SkipUnlessEnabled", StringComparison.Ordinal)
                    && !pattern.IsMatch(text))
                {
                    missing.Add($"{Path.GetRelativePath(testRoot, file)}: {guard} requires Category={category}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"{missing.Count} missing guard/category pair(s); category filters would omit these tests:{Environment.NewLine}  "
            + string.Join($"{Environment.NewLine}  ", missing.OrderBy(m => m, StringComparer.Ordinal)));
    }
}

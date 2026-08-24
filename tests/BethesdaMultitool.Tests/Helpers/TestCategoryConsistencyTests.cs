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
///         This is a source scan rather than a reflection check because the guard call sites are
///         inside method bodies, which reflection cannot see.
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

    public static TheoryData<string> Guards => [.. GuardCategories.Select(g => g.Guard)];

    [Theory]
    [MemberData(nameof(Guards))]
    public void EveryFileCallingAGuard_AlsoCarriesItsCategoryTrait(string guard)
    {
        var category = GuardCategories.Single(g => g.Guard == guard).Category;
        var testRoot = Path.Combine(SourceContract.RepoRoot, "tests", "BethesdaMultitool.Tests");

        // Accept either the shared TestCategories constant or the guard's own Category member —
        // both compile to the same string, and requiring one spelling would be churn, not safety.
        var traitPattern = new Regex(
            @"\[\s*Trait\(\s*""Category""\s*,\s*(?:TestCategories\.\w+|" + Regex.Escape(guard) + @"\.Category)\s*\)\s*\]",
            RegexOptions.Compiled);

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(file) == $"{nameof(TestCategoryConsistencyTests)}.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!text.Contains($"{guard}.SkipUnlessEnabled", StringComparison.Ordinal))
            {
                continue;
            }

            if (!traitPattern.IsMatch(text))
            {
                missing.Add(Path.GetRelativePath(testRoot, file));
            }
        }

        Assert.True(missing.Count == 0,
            $"{missing.Count} file(s) call {guard}.SkipUnlessEnabled() without "
            + $"[Trait(\"Category\", \"{category}\")], so --filter-trait Category={category} "
            + $"would silently skip them:{Environment.NewLine}  "
            + string.Join($"{Environment.NewLine}  ", missing.OrderBy(m => m, StringComparer.Ordinal)));
    }
}

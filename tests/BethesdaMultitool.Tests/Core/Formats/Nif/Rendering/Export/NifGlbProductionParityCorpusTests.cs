using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     The M2.1 per-vertex parity gate: every stratum's files, assembled through the production CLI or GUI route,
///     planned through <c>NifGlbExport.Plan</c> with the normalized preference, encoded by both writers and compared
///     exactly (Layer A) and through decoded accessors (Layer B).
/// </summary>
/// <remarks>
///     <para>
///         Production admits no family yet, so the gate forces the normalized route through the admission seam;
///         it measures what an admission would ship. Each run writes a receipt under
///         <c>TestOutput/nif-glb-parity/</c> before asserting, including the native COLOR_0 accessor types and the
///         tangent divergences decision C9 asks to be measured rather than assumed.
///     </para>
///     <para>
///         Comparison failures and SharedValidation faults must always be zero. Frozen lists, floors, ceilings and
///         feature coverage come from <c>NifGlbParityRatchet.json</c>, which starts empty; an unfrozen stratum only
///         runs the unconditional and stratum-specific rules. <c>BMT_NIF_PARITY_FULL=1</c> inspects every candidate;
///         <c>BMT_REQUIRE_GLTF_VALIDATOR=1</c> makes a missing Khronos validator fail the run.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifGlbProductionParityCorpusTests
{
    /// <summary>
    ///     Optional comma-separated stratum identifiers to run (for example <c>S9,S8</c>), so a memory-constrained host
    ///     can run the small strata first; unset or blank runs every stratum. An unselected row reports skipped.
    /// </summary>
    internal const string StrataVariable = "BMT_NIF_PARITY_STRATA";

    /// <summary>Runs one stratum, writes its receipt and requires every applicable rule to hold.</summary>
    /// <param name="stratumId">The stratum identifier from <c>NifGlbParityCorpus.Strata</c>.</param>
    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    [InlineData("S3")]
    [InlineData("S4")]
    [InlineData("S5")]
    [InlineData("S6-Skyrim")]
    [InlineData("S6-SkyrimSE")]
    [InlineData("S7-FO4")]
    [InlineData("S7-FO76")]
    [InlineData("S7-Starfield")]
    [InlineData("S8")]
    [InlineData("S9")]
    public void Stratum_NormalizedExportMatchesTheNativeWriter(string stratumId)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var selected = Environment.GetEnvironmentVariable(StrataVariable);
        Assert.SkipUnless(IsSelected(selected, stratumId),
            $"{StrataVariable} selects \"{selected}\"; stratum {stratumId} is not among them.");
        var stratum = NifGlbParityCorpus.Stratum(stratumId);
        var root = stratum.ResolveRoot();
        Assert.SkipWhen(root is null, stratum.MissingRootMessage);
        string? supportRoot = null;
        if (stratum.ResolveSupportRoot is { } resolveSupport)
        {
            supportRoot = resolveSupport();
            Assert.SkipWhen(supportRoot is null,
                $"{stratum.Description} supporting root not found ({stratum.SupportDescription}). " +
                RealAssetPaths.SkipMessage("FNV extracted texture data"));
        }

        var validator = NifGlbKhronosValidator.FindExecutable();
        Assert.False(NifGlbKhronosValidator.IsRequired && validator is null,
            $"{NifGlbKhronosValidator.RequireVariable}=1 but no Khronos glTF validator was found.");

        var receipt = NifGlbParityCorpus.Run(stratum, root, supportRoot, validator, NifGlbParityCorpus.FullStratum,
            TestContext.Current.CancellationToken);
        var violations = NifGlbParityCorpus.Violations(stratum, receipt,
            NifGlbParityRatchet.Load().For(stratum.Id));
        var receiptPath = NifGlbParityCorpus.WriteReceipt(receipt);
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine(
            $"{stratum.Id} {stratum.Description}: candidates {receipt.Candidates}, inspected {receipt.Inspected}, " +
            $"compared {receipt.Compared}, passed {receipt.Passed}, declined {receipt.Declines.Values.Sum()}, " +
            $"parse errors {receipt.ParseErrors.Count}, no scene {receipt.NoScene.Count}, " +
            $"shared validation {receipt.SharedValidation}, failures {receipt.ComparisonFailures}; " +
            $"validator {receipt.Validator}; ratchet {receipt.Ratchet}; receipt {receiptPath}");
        foreach (var (key, count) in receipt.NativeColorAccessors)
        {
            output?.WriteLine($"  native COLOR_0 {key}: {count}");
        }

        foreach (var (key, count) in receipt.TangentDivergences)
        {
            output?.WriteLine($"  tangent divergence {key}: {count}");
        }

        foreach (var (reason, count) in receipt.Declines)
        {
            output?.WriteLine($"  declined {count}x: {reason}");
        }

        Assert.True(violations.Count == 0,
            $"{stratum.Id}: {string.Join(Environment.NewLine, violations)}{Environment.NewLine}Receipt: {receiptPath}");
    }

    /// <summary>Whether a stratum is selected by the optional <see cref="StrataVariable" /> list.</summary>
    /// <param name="selection">The raw variable value; null or blank selects every stratum.</param>
    /// <param name="stratumId">The row's stratum identifier.</param>
    /// <returns>True when the stratum should run.</returns>
    internal static bool IsSelected(string? selection, string stratumId) =>
        string.IsNullOrWhiteSpace(selection) ||
        selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(stratumId, StringComparer.OrdinalIgnoreCase);

    /// <summary>The stratum filter selects every stratum when unset and exactly the listed ones otherwise.</summary>
    [Fact]
    public void StrataSelection_IsAllWhenUnsetAndExactOtherwise()
    {
        Assert.True(IsSelected(null, "S1"));
        Assert.True(IsSelected("  ", "S7-FO4"));
        Assert.True(IsSelected("S9, s8", "S8"));
        Assert.False(IsSelected("S9,S8", "S1"));
        Assert.False(IsSelected("S6", "S6-Skyrim"));
    }
}

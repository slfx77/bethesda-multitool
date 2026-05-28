using Xunit;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Gate for tests that genuinely depend on real Fallout game assets
///     (Sample/Full_Builds/*, Sample/ESM/*, Sample/MemoryDump/* etc.) and
///     can't be expressed with synthetic byte fixtures. These tests preserve
///     regression coverage for real-asset workflows (NPC rendering, DDX
///     carving, dialogue provenance, dump-resident state) but are skipped
///     in default CI runs because:
///     <list type="bullet">
///         <item>Most CI environments don't have the assets available.</item>
///         <item>Real-asset loads are slow (10s-minutes per test).</item>
///         <item>The test discipline standard prefers synthetic data when
///         feasible — these tests stay opt-in to make the asset
///         dependency explicit.</item>
///     </list>
///     <para>
///         Enable with <c>RUN_BUCKET_B=1</c> environment variable when
///         changing the relevant code (rendering pipeline, DDX carver,
///         dialogue provenance inspector, semantic loader, etc.).
///     </para>
///     <para>
///         New tests added after Tier 8 should NOT use this guard — they
///         should be synthetic per <c>feedback_test_discipline</c>. This
///         guard exists to contain inherited real-asset debt, not to
///         enable new real-asset tests.
///     </para>
/// </summary>
internal static class BucketBTestGuard
{
    public const string Category = "BucketB";
    private const string EnabledValue = "1";

    public static void SkipUnlessEnabled()
    {
        var value = Environment.GetEnvironmentVariable("RUN_BUCKET_B");
        Assert.SkipWhen(
            !string.Equals(value, EnabledValue, StringComparison.Ordinal),
            "Bucket B tests are disabled by default — they require real Fallout game assets. "
            + "Set RUN_BUCKET_B=1 to run them.");
    }
}

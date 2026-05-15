using Xunit;

namespace FalloutXbox360Utils.Tests.Helpers;

internal static class GpuTestGuard
{
    public const string Category = "Gpu";
    private const string EnabledValue = "1";

    public static void SkipUnlessEnabled()
    {
        var value = Environment.GetEnvironmentVariable("RUN_GPU_TESTS");
        Assert.SkipWhen(
            !string.Equals(value, EnabledValue, StringComparison.Ordinal),
            "GPU tests are disabled by default. Set RUN_GPU_TESTS=1 to run them.");
    }
}

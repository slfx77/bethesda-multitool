using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Gate for tests that invoke the real D3D shader compiler (Vortice D3DCompile). Compilation
///     needs no GPU device but is the single most CPU-expensive operation in the default suite
///     (~50 vs/ps/cs_5_1 compiles), and the entry points it validates rarely regress outside
///     shader work. Opt in with <c>RUN_SHADER_COMPILE_TESTS=1</c> when touching HLSL or the
///     renderer's shader plumbing. Source-text contract assertions stay ungated — only the
///     compile step itself sits behind this guard.
/// </summary>
internal static class ShaderCompileTestGuard
{
    public const string Category = "ShaderCompile";
    private const string EnabledValue = "1";

    public static void SkipUnlessEnabled()
    {
        var value = Environment.GetEnvironmentVariable("RUN_SHADER_COMPILE_TESTS");
        Assert.SkipWhen(
            !string.Equals(value, EnabledValue, StringComparison.Ordinal),
            "Shader-compile tests are disabled by default — they invoke the real D3D compiler. "
            + "Set RUN_SHADER_COMPILE_TESTS=1 to run them.");
    }
}

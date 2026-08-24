namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     The complete set of <c>[Trait("Category", ...)]</c> values used by this suite.
///     <para>
///         Two kinds of category live here. The first three mirror an opt-in execution guard
///         (<see cref="BucketBTestGuard" />, <see cref="GpuTestGuard" />,
///         <see cref="ShaderCompileTestGuard" />) and exist so <c>--filter-trait</c> selects
///         exactly the tests that guard skips — a trait that disagrees with its guard makes
///         filtered runs silently under-select, which is how a "targeted" sweep ends up
///         exercising nothing.
///     </para>
///     <para>
///         The last two mark methods that are shaped like tests but are not correctness gates:
///         <see cref="Benchmark" /> measures wall-clock or allocation rates, and
///         <see cref="Tool" /> generates artifacts. Neither should ever be read as proof that
///         behaviour is correct, so both must stay filterable.
///     </para>
///     Reference these constants rather than repeating the literal — the guards' own
///     <c>Category</c> members are the source of truth for the first three.
/// </summary>
internal static class TestCategories
{
    /// <summary>Needs real retail game assets; gated by <c>RUN_BUCKET_B=1</c>.</summary>
    public const string BucketB = BucketBTestGuard.Category;

    /// <summary>Creates a real D3D device; gated by <c>RUN_GPU_TESTS=1</c>.</summary>
    public const string Gpu = GpuTestGuard.Category;

    /// <summary>Invokes the real D3D shader compiler; gated by <c>RUN_SHADER_COMPILE_TESTS=1</c>.</summary>
    public const string ShaderCompile = ShaderCompileTestGuard.Category;

    /// <summary>A measurement run, not a correctness assertion. Advisory ceilings only.</summary>
    public const string Benchmark = "Benchmark";

    /// <summary>A generator that produces an artifact and asserts nothing.</summary>
    public const string Tool = "Tool";
}

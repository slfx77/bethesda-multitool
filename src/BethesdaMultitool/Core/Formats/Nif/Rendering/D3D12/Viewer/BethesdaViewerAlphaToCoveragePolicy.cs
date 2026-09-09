namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;

internal enum BethesdaViewerAlphaToCoverageMode
{
    Hardware,
    BlendFallbackSingleSample,
    BlendFallbackDisabled
}

/// <summary>
///     Viewer-only policy for native hair/brow/lash alpha-to-coverage. The environment override is
///     deliberately narrower than scene MSAA so diagnostics can retain the 4x target + resolve path
///     while routing only authored A2C parts through the established blend fallback.
/// </summary>
internal static class BethesdaViewerAlphaToCoveragePolicy
{
    internal const string EnvironmentVariable = "FALLOUT_VIEWER_NATIVE_A2C";

    internal static BethesdaViewerAlphaToCoverageMode Resolve(
        bool pipelineAvailable,
        string? overrideValue)
    {
        if (!pipelineAvailable)
        {
            return BethesdaViewerAlphaToCoverageMode.BlendFallbackSingleSample;
        }

        return overrideValue == "0"
            ? BethesdaViewerAlphaToCoverageMode.BlendFallbackDisabled
            : BethesdaViewerAlphaToCoverageMode.Hardware;
    }

    internal static string? DescribeFallback(BethesdaViewerAlphaToCoverageMode mode)
    {
        return mode switch
        {
            BethesdaViewerAlphaToCoverageMode.BlendFallbackSingleSample =>
                "the scene target is single-sampled",
            BethesdaViewerAlphaToCoverageMode.BlendFallbackDisabled =>
                $"native A2C is disabled by {EnvironmentVariable}=0",
            _ => null
        };
    }
}

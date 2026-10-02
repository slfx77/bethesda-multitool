using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Borrowed optional modern-water graphics and ordered compute pipeline handles.</summary>
/// <remarks>The creating ShaderPipelineResources family owns every native handle in this result.</remarks>
internal sealed class ModernWaterPipelineSet12
{
    /// <summary>Gets the ordinary depth-test graphics permutation.</summary>
    internal required ID3D12PipelineState Pixel { get; init; }

    /// <summary>Gets the read-only depth-sample graphics permutation.</summary>
    internal required ID3D12PipelineState PixelDepthSample { get; init; }

    /// <summary>Gets the body/coverage, normal, gloss and depth-LUT compute permutations, in output order.</summary>
    internal required ID3D12PipelineState[] ComputePipelines { get; init; }
}

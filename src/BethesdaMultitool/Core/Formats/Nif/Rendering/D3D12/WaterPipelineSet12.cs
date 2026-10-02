using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Borrowed water pipelines and independent base descriptions used for optional modern permutations.</summary>
/// <remarks>The creating ShaderPipelineResources family owns every native handle in this result.</remarks>
internal sealed class WaterPipelineSet12
{
    /// <summary>Gets the borrowed three-layer scrolling and scalar noise blending compute pipeline.</summary>
    internal required ID3D12PipelineState NoiseScrollBlend { get; init; }

    /// <summary>Gets the borrowed Sobel noise-to-packed-normal compute pipeline.</summary>
    internal required ID3D12PipelineState NoiseNormal { get; init; }

    /// <summary>Gets the borrowed box-average and renormalization compute pipeline for noise-normal mip levels.</summary>
    internal required ID3D12PipelineState NoiseDownsample { get; init; }

    /// <summary>Gets the borrowed Fallout 3/New Vegas base water graphics pipeline.</summary>
    internal required ID3D12PipelineState Water { get; init; }

    /// <summary>Gets the borrowed Oblivion water graphics pipeline.</summary>
    internal required ID3D12PipelineState Oblivion { get; init; }

    /// <summary>Gets the borrowed Fallout 4 standard water graphics pipeline.</summary>
    internal required ID3D12PipelineState Fo4 { get; init; }

    /// <summary>Gets the borrowed Fallout 76 float-optics water pipeline with dual-source RGB transmission.</summary>
    internal required ID3D12PipelineState Fo76Optics { get; init; }

    /// <summary>Gets the borrowed Morrowind animated-texture water graphics pipeline.</summary>
    internal required ID3D12PipelineState Morrowind { get; init; }

    /// <summary>Gets the borrowed Starfield source-backed approximate water graphics pipeline.</summary>
    internal required ID3D12PipelineState Starfield { get; init; }

    /// <summary>Gets the borrowed flat water fallback for games without a recovered shader.</summary>
    internal required ID3D12PipelineState Flat { get; init; }

    /// <summary>Gets the borrowed Fallout 3/New Vegas water pipeline with hardware occlusion and read-only scene depth.</summary>
    internal required ID3D12PipelineState DepthSample { get; init; }

    /// <summary>Gets the borrowed New Vegas WATER001 pipeline consuming depth and the opaque-scene snapshot.</summary>
    internal required ID3D12PipelineState FnvWater001DepthSample { get; init; }

    /// <summary>Gets the borrowed Skyrim refraction pipeline writing an opaque snapshot-derived color.</summary>
    internal required ID3D12PipelineState SkyrimOpaqueSnapshotDepthSample { get; init; }

    /// <summary>Gets the borrowed Oblivion water pipeline with hardware occlusion and read-only scene depth.</summary>
    internal required ID3D12PipelineState OblivionDepthSample { get; init; }

    /// <summary>Gets the borrowed Fallout 4 water pipeline with hardware occlusion and read-only scene depth.</summary>
    internal required ID3D12PipelineState Fo4DepthSample { get; init; }

    /// <summary>Gets the borrowed Fallout 76 dual-source optics pipeline with read-only scene depth.</summary>
    internal required ID3D12PipelineState Fo76OpticsDepthSample { get; init; }

    /// <summary>Gets the borrowed Morrowind water pipeline with hardware occlusion and read-only scene depth.</summary>
    internal required ID3D12PipelineState MorrowindDepthSample { get; init; }

    /// <summary>Gets the borrowed Starfield approximate water pipeline with read-only scene depth.</summary>
    internal required ID3D12PipelineState StarfieldDepthSample { get; init; }

    /// <summary>Gets the independent ordinary depth-test description used by the modern-water factory.</summary>
    internal required GraphicsPipelineStateDescription DepthTemplate { get; init; }

    /// <summary>Gets the independent read-only depth-sample description used by the modern-water factory.</summary>
    internal required GraphicsPipelineStateDescription DepthSampleTemplate { get; init; }

}

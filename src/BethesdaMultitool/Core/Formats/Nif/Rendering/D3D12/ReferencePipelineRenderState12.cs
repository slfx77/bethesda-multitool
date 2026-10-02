using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Fixed-function choices shared by native reference pipeline construction.</summary>
/// <param name="DoubleSided">Disables back-face culling when true.</param>
/// <param name="BlendAttachment">Authored blend attachment, or null for opaque output.</param>
/// <param name="DepthWriteEnabled">Enables writes through the reversed-Z depth test.</param>
/// <param name="Decal">Applies the established positive depth bias for coplanar overlays.</param>
/// <param name="AlphaToCoverage">Enables multisample coverage derived from shader alpha.</param>
/// <param name="DepthTestEnabled">Enables the GreaterEqual reversed-Z comparison.</param>
/// <param name="MirrorWinding">Flips front-face winding for the reflected-view replay.</param>
internal readonly record struct ReferencePipelineRenderState12(
    bool DoubleSided,
    RenderTargetBlendDescription? BlendAttachment,
    bool DepthWriteEnabled,
    bool Decal = false,
    bool AlphaToCoverage = false,
    bool DepthTestEnabled = true,
    bool MirrorWinding = false);

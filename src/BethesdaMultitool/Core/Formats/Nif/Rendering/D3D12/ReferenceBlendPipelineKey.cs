namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Preserves authored blend bytes and draw-state identity within one shader route and depth-write cache.</summary>
/// <param name="SrcBlendMode">Unmodified source blend factor byte.</param>
/// <param name="DstBlendMode">Unmodified destination blend factor byte.</param>
/// <param name="DoubleSided">Whether culling is disabled.</param>
/// <param name="Decal">Whether coplanar overlay bias is enabled.</param>
/// <param name="DepthTestOff">Whether authored state disables depth testing.</param>
internal readonly record struct ReferenceBlendPipelineKey(
    byte SrcBlendMode, byte DstBlendMode, bool DoubleSided, bool Decal, bool DepthTestOff = false);

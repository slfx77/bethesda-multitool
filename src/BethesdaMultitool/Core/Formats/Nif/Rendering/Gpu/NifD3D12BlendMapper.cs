#if WINDOWS_GUI
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Maps NiAlphaProperty (OpenGL-order) blend-mode bytes to their Direct3D 12 <c>Blend</c> equivalents.</summary>
internal static class NifD3D12BlendMapper
{
    internal static D12.Blend ResolveBlendFactor(byte mode)
    {
        // NIF alpha property blend modes follow OpenGL enumeration order.
        return mode switch
        {
            0 => D12.Blend.One,
            1 => D12.Blend.Zero,
            2 => D12.Blend.SourceColor,
            3 => D12.Blend.InverseSourceColor,
            4 => D12.Blend.DestinationColor,
            5 => D12.Blend.InverseDestinationColor,
            6 => D12.Blend.SourceAlpha,
            7 => D12.Blend.InverseSourceAlpha,
            8 => D12.Blend.DestinationAlpha,
            9 => D12.Blend.InverseDestinationAlpha,
            10 => D12.Blend.One, // GL_SRC_ALPHA_SATURATE: no D3D12 equivalent; approximate as One.
            _ => D12.Blend.SourceAlpha
        };
    }
}
#endif

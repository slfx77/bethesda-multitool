using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

internal enum NifD3D12BlendOperation
{
    Standard = 0,
    Additive = 1,
    Multiplicative = 2
}

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
            10 => D12.Blend.SourceAlphaSaturate,
            _ => D12.Blend.SourceAlpha
        };
    }

    /// <summary>
    ///     Classifies blend equations whose fog neutral and HDR range differ from ordinary alpha
    ///     compositing. The two multiplicative pairs are algebraically equivalent:
    ///     <c>ZERO, SRC_COLOR</c> and <c>DST_COLOR, ZERO</c> both produce <c>dst * src</c>.
    /// </summary>
    internal static NifD3D12BlendOperation ClassifyOperation(
        bool alphaBlend,
        byte sourceMode,
        byte destinationMode)
    {
        if (!alphaBlend)
        {
            return NifD3D12BlendOperation.Standard;
        }

        if ((sourceMode == 1 && destinationMode == 2) ||
            (sourceMode == 4 && destinationMode == 1))
        {
            return NifD3D12BlendOperation.Multiplicative;
        }

        return destinationMode is 0 or 10
            ? NifD3D12BlendOperation.Additive
            : NifD3D12BlendOperation.Standard;
    }
}

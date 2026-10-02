using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Identifies only native pipeline state; alpha testing and coverage remain per-draw shader inputs.</summary>
internal readonly record struct GpuSpritePipelineKey
{
    /// <summary>Four cull/shader choices for each of 121 blend pairs and one nonblended state.</summary>
    internal const int Capacity = 4 * (11 * 11 + 1);

    /// <summary>Whether blending replaces the opaque depth-write state.</summary>
    internal bool Blended { get; private init; }
    /// <summary>Canonical NIF source factor, retaining the mapper's unknown-byte fallback.</summary>
    internal byte SourceBlend { get; private init; }
    /// <summary>Canonical NIF destination factor, ignored when blending is disabled.</summary>
    internal byte DestinationBlend { get; private init; }
    /// <summary>Whether native back-face culling is disabled.</summary>
    internal bool DoubleSided { get; private init; }
    /// <summary>Whether the classic skin pixel shader is selected.</summary>
    internal bool ClassicSkin { get; private init; }
    /// <summary>Stable bounded ownership slot, independent of insertion order.</summary>
    internal int Slot => (Blended ? 1 + SourceBlend * 11 + DestinationBlend : 0) * 4
                         + (DoubleSided ? 1 : 0) + (ClassicSkin ? 2 : 0);

    /// <summary>Collapses source distinctions that produce identical native descriptions.</summary>
    /// <param name="mode">Source alpha mode; only Blend enables native blending.</param>
    /// <param name="source">Source factor in the existing NIF mapper's byte domain.</param>
    /// <param name="destination">Destination factor in that same domain.</param>
    /// <param name="doubleSided">Whether to disable back-face culling.</param>
    /// <param name="classicSkin">Whether to use the classic skin shader permutation.</param>
    /// <returns>A key whose slot covers every currently representable native state without collisions.</returns>
    internal static GpuSpritePipelineKey Create(NifAlphaRenderMode mode, byte source,
        byte destination, bool doubleSided, bool classicSkin)
    {
        var blended = mode == NifAlphaRenderMode.Blend;
        return new GpuSpritePipelineKey
        {
            Blended = blended,
            SourceBlend = blended ? NormalizeBlend(source) : (byte)0,
            DestinationBlend = blended ? NormalizeBlend(destination) : (byte)0,
            DoubleSided = doubleSided,
            ClassicSkin = classicSkin
        };
    }

    /// <summary>Retains known factors and the production mapper's SourceAlpha fallback.</summary>
    /// <param name="mode">Original source factor byte.</param>
    /// <returns>A canonical factor in the eleven supported native states.</returns>
    private static byte NormalizeBlend(byte mode) => mode <= 10 ? mode : (byte)6;
}

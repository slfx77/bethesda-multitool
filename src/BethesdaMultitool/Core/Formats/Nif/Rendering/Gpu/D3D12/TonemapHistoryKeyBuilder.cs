using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Stable identity for eye-adaptation history. Recovered FNV
///     <c>ImageSpaceEffectHDR::UpdateParams</c> clears its adapted target only when the explicit
///     <c>bClearAdaptedLight</c> byte is set; routine CELL, worldspace, IMGS, weather, interior, and
///     modifier changes do not participate. The GPU pass separately handles mechanical target and
///     adaptive-mode invalidation.
/// </summary>
internal static class TonemapHistoryKeyBuilder
{
    internal static ulong Build(BethesdaGame game, ulong explicitClearGeneration)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var key = offset;
        key = (key ^ (uint)game) * prime;
        key = (key ^ explicitClearGeneration) * prime;
        return key;
    }
}

using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Stable identity for eye-adaptation history. Weather transition progress is deliberately
///     excluded: the current/outgoing source pair owns one continuous adaptation history, while a
///     source-pair, context, image-space, or toggle change starts a new one.
/// </summary>
internal static class TonemapHistoryKeyBuilder
{
    internal static ulong Build(
        BethesdaGame game,
        uint contextId,
        uint imageSpaceId,
        uint currentWeatherId,
        uint outgoingWeatherId,
        bool interior,
        bool hdrEnabled,
        bool modifiersEnabled)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var key = offset;
        key = (key ^ (uint)game) * prime;
        key = (key ^ contextId) * prime;
        key = (key ^ imageSpaceId) * prime;
        key = (key ^ currentWeatherId) * prime;
        key = (key ^ outgoingWeatherId) * prime;
        key = (key ^ (interior ? 1u : 0u)) * prime;
        key = (key ^ (hdrEnabled ? 1u : 0u)) * prime;
        key = (key ^ (modifiersEnabled ? 1u : 0u)) * prime;
        return key;
    }
}

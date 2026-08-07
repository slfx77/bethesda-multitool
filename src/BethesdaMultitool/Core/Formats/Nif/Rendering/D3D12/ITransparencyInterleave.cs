namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Merge hook for the unified transparency stream. The reference renderer's blended loop calls
///     <see cref="DrainDownTo" /> before each of its own draws so every water batch at least as far
///     as that draw renders first — the two-stream equivalent of the engine's single depth-sorted
///     alpha pass (both sequences are individually sorted farthest-first on the SAME clip-w metric,
///     so the merge is exact). The host drains whatever remains after the blended loop finishes.
/// </summary>
internal interface ITransparencyInterleave
{
    /// <summary>Draws every queued water batch with view-axis depth ≥ <paramref name="depth" />.
    /// Returns true when at least one batch drew — the caller must then rebind its shared frame
    /// state (per-frame CBV, bindless tables) before its next draw.</summary>
    bool DrainDownTo(float depth);
}

using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Identifies a per-cell, per-quadrant, per-ATXT-layer opacity grid.
///     <see cref="LayerOrdinal" /> is the ATXT's <c>Layer</c> field; pairing it with
///     <see cref="Quadrant" /> uniquely identifies the blend grid within a cell.
/// </summary>
internal readonly record struct OpacityCacheKey(int Gx, int Gy, byte Quadrant, ushort LayerOrdinal);

/// <summary>
///     Static helpers for the 17×17 VTXT opacity grid. The D3D12 cache (and the unit tests)
///     share <see cref="BuildOpacityGrid" /> for the CPU-side rasterization; the GPU upload
///     itself is owned by the per-backend cache type.
/// </summary>
internal static class TerrainOpacityTextureCache
{
    /// <summary>VTXT positions are <c>j*17 + i</c> in [0, 17*17-1] = [0, 288].</summary>
    public const int Grid = 17;
    public const int GridSize = Grid * Grid;

    /// <summary>
    ///     Pure CPU rasterization of a <see cref="LandTextureLayer.BlendEntries" /> list into a
    ///     17×17 byte grid. Out-of-range positions are silently dropped; opacities are clamped
    ///     to <c>[0, 1]</c> and scaled to <c>[0, 255]</c>. The destination span is zero-initialized
    ///     first — pixels not touched by an entry stay 0 (fully transparent).
    /// </summary>
    public static void BuildOpacityGrid(LandTextureLayer atxtLayer, Span<byte> destination)
    {
        if (destination.Length < GridSize)
            throw new ArgumentException($"Destination must be at least {GridSize} bytes.", nameof(destination));

        destination[..GridSize].Clear();
        foreach (var entry in atxtLayer.BlendEntries)
        {
            if (entry.Position >= GridSize) continue;
            var clamped = Math.Clamp(entry.Opacity, 0f, 1f);
            destination[entry.Position] = (byte)(clamped * 255f);
        }
    }
}

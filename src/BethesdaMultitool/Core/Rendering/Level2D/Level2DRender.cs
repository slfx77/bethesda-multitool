// The 2D-level seam (Level2DLayer / Level2DRender / ILevel2DSource) is ported from
//   NeversoftMultitool —
//   src/NeversoftMultitool/Core/Rendering/Level2d/Level2dRender.cs (upstream spells it "Level2d";
//   the names carry a capital D here to satisfy this repo's S101 rule). Its layer set is retargeted
//   from that project's collision-vs-art split to the classic voxel/heightmap layers this repo
//   reads. License texts are collected centrally in THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>Which picture of a level a 2D view is showing.</summary>
internal enum Level2DLayer
{
    /// <summary>The floor plane — Arena's <c>FLOR</c>, or a heightmap's terrain.</summary>
    Floor,

    /// <summary>The first wall/object plane — Arena's <c>MAP1</c>.</summary>
    Walls,

    /// <summary>The second wall/object plane — Arena's <c>MAP2</c>.</summary>
    Ceiling,

    /// <summary>A single-plane overlay, such as Daggerfall's CLIMATE/POLITIC PAK.</summary>
    Overlay
}

/// <summary>One rendered 2D level image: row-major RGBA, 4 bytes per pixel.</summary>
/// <remarks>
///     Wraps the producer's buffer rather than copying it — a rendered level runs to millions of
///     pixels (Daggerfall's WOODS heightmap alone is 1000x500 before any scale factor).
/// </remarks>
internal readonly record struct Level2DRender(int Width, int Height, byte[] Rgba);

/// <summary>
///     A level that can be shown without converting it to 3D.
/// </summary>
/// <remarks>
///     Most level formats are geometry and have no such picture; this exists for the ones authored
///     as a grid. The classic catalogue is the case that motivates it here — an Arena <c>.MIF</c>
///     IS a voxel grid and a Daggerfall <c>WOODS.WLD</c> IS a heightmap, so building a 3D surface
///     to look at one is a detour through a lossier representation.
///     Sources that own external assets also implement <see cref="IDisposable" />; presenters
///     release them when replaced, cleared, or discarded before display.
/// </remarks>
internal interface ILevel2DSource
{
    /// <summary>The layers this level can render, in display order. Never empty.</summary>
    IReadOnlyList<Level2DLayer> Layers { get; }

    /// <summary>A short name for the level, for export stems and status lines.</summary>
    string DisplayName { get; }

    /// <summary>
    ///     Render one layer, or null when this level cannot produce it. Callers must treat null as
    ///     "nothing to show", never as an error.
    /// </summary>
    Level2DRender? Render(Level2DLayer layer);
}

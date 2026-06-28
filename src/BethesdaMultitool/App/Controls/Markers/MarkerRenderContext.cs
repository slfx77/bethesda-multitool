using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     Everything the world-map marker draw needs to render markers for the loaded game: the
///     <see cref="Game" /> (so <c>MapMarkerCatalog</c> resolves the right per-type name/glyph/color) and
///     the ready-to-draw <see cref="Icons" /> keyed by raw <c>TNAM</c> value (tinted for the embedded
///     set, raw atlas crops otherwise, or null/empty ⇒ glyph dots).
/// </summary>
internal readonly record struct MarkerRenderContext(
    BethesdaGame Game,
    IReadOnlyDictionary<int, CanvasBitmap>? Icons);

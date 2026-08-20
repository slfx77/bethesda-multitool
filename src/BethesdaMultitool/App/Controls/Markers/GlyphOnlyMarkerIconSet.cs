using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     The no-art fallback set: carries no bitmaps, so the map draws every marker as its
///     <c>MapMarkerCatalog</c> glyph/color dot. Used for games whose authentic art isn't wired and for
///     games with unknown taxonomy.
/// </summary>
internal sealed class GlyphOnlyMarkerIconSet(BethesdaGame game) : IMapMarkerIconSet
{
    private static readonly IReadOnlyDictionary<int, CanvasBitmap> NoIcons = new Dictionary<int, CanvasBitmap>();

    public BethesdaGame Game { get; } = game;
    public bool RequiresTinting => false;
    public IReadOnlyDictionary<int, CanvasBitmap> Icons => NoIcons;

    public void Dispose()
    {
    }
}

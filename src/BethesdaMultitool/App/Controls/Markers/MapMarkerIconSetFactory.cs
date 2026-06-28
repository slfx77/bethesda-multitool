using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     Builds the right <see cref="IMapMarkerIconSet" /> for a game from its
///     <see cref="GameProfile.MarkerArt" /> strategy. FO3/FNV get the embedded tinted set; everyone else
///     is glyph-only until their atlas phase adds an <c>AtlasMarkerIconSet</c> case here.
/// </summary>
internal static class MapMarkerIconSetFactory
{
    public static IMapMarkerIconSet Create(BethesdaGame game, ICanvasResourceCreator resourceCreator) =>
        GameProfiles.For(game).MarkerArt switch
        {
            MarkerArtStrategy.EmbeddedTinted => new EmbeddedMarkerIconSet(game, resourceCreator),
            _ => new GlyphOnlyMarkerIconSet(game)
        };
}

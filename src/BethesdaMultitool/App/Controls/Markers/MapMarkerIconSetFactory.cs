using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     Builds the right <see cref="IMapMarkerIconSet" /> for a game from its
///     <see cref="GameProfile.MarkerArt" /> strategy. Embedded games (FO3/FNV tinted; Skyrim/FO4/FO76
///     pre-styled) share the catalog-driven <see cref="EmbeddedMarkerIconSet" />; the rest are glyph-only
///     until their icons are extracted + wired.
/// </summary>
internal static class MapMarkerIconSetFactory
{
    public static IMapMarkerIconSet Create(BethesdaGame game, ICanvasResourceCreator resourceCreator) =>
        GameProfiles.For(game).MarkerArt switch
        {
            MarkerArtStrategy.EmbeddedTinted or MarkerArtStrategy.EmbeddedColored =>
                new EmbeddedMarkerIconSet(game, resourceCreator),
            _ => new GlyphOnlyMarkerIconSet(game)
        };
}

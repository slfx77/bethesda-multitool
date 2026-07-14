using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     Builds the right <see cref="IMapMarkerIconSet" /> for a loaded world from its
///     <see cref="GameProfile.MarkerArt" /> strategy. Authentic-art games use an archive-first set with
///     the bundled icons as fallback; the rest are glyph-only until their taxonomy/art is wired.
/// </summary>
internal static class MapMarkerIconSetFactory
{
    public static IMapMarkerIconSet Create(WorldViewData? data, ICanvasResourceCreator resourceCreator)
    {
        var game = data?.Game ?? BethesdaGame.Unknown;
        return GameProfiles.For(game).MarkerArt switch
        {
            MarkerArtStrategy.EmbeddedTinted or MarkerArtStrategy.EmbeddedColored =>
                new ArchiveFirstMarkerIconSet(game, resourceCreator, EnumerateSourcePaths(data)),
            _ => new GlyphOnlyMarkerIconSet(game)
        };

        static IEnumerable<string?> EnumerateSourcePaths(WorldViewData? worldData)
        {
            if (worldData is null)
            {
                yield break;
            }

            yield return worldData.SourceFilePath;
            if (worldData.AdditionalDataPaths is not null)
            {
                foreach (var path in worldData.AdditionalDataPaths)
                {
                    yield return path;
                }
            }
        }
    }
}

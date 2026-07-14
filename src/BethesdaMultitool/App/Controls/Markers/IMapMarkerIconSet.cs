using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     Per-game source of 2D world-map marker icon bitmaps, keyed by the raw <c>TNAM</c> marker value.
///     The map builds one set per loaded game (see <see cref="MapMarkerIconSetFactory" />) and rebuilds
///     it on a game change. A raw value with no entry in <see cref="Icons" /> is drawn as the
///     <c>MapMarkerCatalog</c> glyph/color dot instead.
/// </summary>
internal interface IMapMarkerIconSet : IDisposable
{
    /// <summary>The game these icons belong to (used to detect when a rebuild is needed).</summary>
    BethesdaGame Game { get; }

    /// <summary>
    ///     True for white-silhouette art (FO3/FNV/FO4), which must be tinted to the map color scheme at
    ///     draw time. Oblivion/Skyrim/FO76 art is already styled, so it is drawn untinted.
    /// </summary>
    bool RequiresTinting { get; }

    /// <summary>Base icon bitmaps by raw <c>TNAM</c> value. Empty ⇒ the map draws glyph/color dots.</summary>
    IReadOnlyDictionary<int, CanvasBitmap> Icons { get; }
}

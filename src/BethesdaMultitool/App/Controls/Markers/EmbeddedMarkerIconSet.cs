using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     Marker icons that ship embedded in the tool, keyed by raw <c>TNAM</c> value. Catalog-driven: it
///     loads <see cref="MapMarkerEntry.IconKey" /> for each entry in the game's
///     <see cref="MapMarkerCatalog" /> table, so it serves every game whose art was extracted offline —
///     FO3/FNV (white silhouettes, tinted to the map color scheme) and Skyrim/FO4/FO76 (pre-styled,
///     untinted). Because the art is embedded, it also works for memory dumps with no game assets.
///     Decoded once on construction; this runs inside the Win2D draw handler on the STA UI thread, so the
///     decode uses the non-pumping wait pattern (a plain <c>GetResult()</c> there can re-enter XAML).
/// </summary>
internal sealed class EmbeddedMarkerIconSet : IMapMarkerIconSet
{
    private readonly Dictionary<int, CanvasBitmap> _icons = new();

    public EmbeddedMarkerIconSet(BethesdaGame game, ICanvasResourceCreator resourceCreator)
    {
        Game = game;
        RequiresTinting = GameProfiles.For(game).MarkersAreTinted;

        foreach (var entry in MapMarkerCatalog.For(game))
        {
            var png = MapMarkerIconProvider.GetIconPng(entry.IconKey);
            if (png is null) continue;

            using var ms = new MemoryStream(png);
            var loadTask = CanvasBitmap.LoadAsync(resourceCreator, ms.AsRandomAccessStream()).AsTask();
            Core.Orchestration.NonPumpingWait.Wait(loadTask);
            _icons[entry.RawValue] = loadTask.GetAwaiter().GetResult();
        }
    }

    public BethesdaGame Game { get; }
    public bool RequiresTinting { get; }
    public IReadOnlyDictionary<int, CanvasBitmap> Icons => _icons;

    public void Dispose()
    {
        foreach (var bmp in _icons.Values) bmp.Dispose();
        _icons.Clear();
    }
}

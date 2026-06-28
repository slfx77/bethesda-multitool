using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     FO3/FNV marker icons: the bundled white-silhouette PNGs from <see cref="MapMarkerIconProvider" />,
///     keyed by raw value (== <see cref="MapMarkerType" /> for these games). Monochrome, so the map
///     tints them to its color scheme at draw time (<see cref="RequiresTinting" /> is true). Decoded
///     once on construction — this runs inside the Win2D draw handler on the STA UI thread, so the
///     decode uses the non-pumping wait pattern (a plain <c>GetResult()</c> there can re-enter XAML).
/// </summary>
internal sealed class EmbeddedMarkerIconSet : IMapMarkerIconSet
{
    private readonly Dictionary<int, CanvasBitmap> _icons = new();

    public EmbeddedMarkerIconSet(BethesdaGame game, ICanvasResourceCreator resourceCreator)
    {
        Game = game;
        foreach (var type in Enum.GetValues<MapMarkerType>())
        {
            if (type == MapMarkerType.None) continue;
            var png = MapMarkerIconProvider.GetIconPng(type);
            if (png is null) continue;

            using var ms = new MemoryStream(png);
            var loadTask = CanvasBitmap.LoadAsync(resourceCreator, ms.AsRandomAccessStream()).AsTask();
            Core.Orchestration.NonPumpingWait.Wait(loadTask);
            _icons[(int)type] = loadTask.GetAwaiter().GetResult();
        }
    }

    public BethesdaGame Game { get; }
    public bool RequiresTinting => true;
    public IReadOnlyDictionary<int, CanvasBitmap> Icons => _icons;

    public void Dispose()
    {
        foreach (var bmp in _icons.Values) bmp.Dispose();
        _icons.Clear();
    }
}

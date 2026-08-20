using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Games;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Foundation;

namespace BethesdaMultitool;

/// <summary>
///     UI Automation support for the custom-drawn world map. Map markers are pixels inside one
///     Win2D canvas rather than XAML elements, so the default peer cannot expose their names,
///     clickable bounds, or invoke action. This peer projects the currently-visible markers as
///     virtual button children backed by the same screen-space metrics used for drawing/hit tests.
/// </summary>
public sealed partial class WorldMapControl
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new WorldMapControlAutomationPeer(this);

    internal IReadOnlyList<MapMarkerAutomationEntry> GetVisibleMarkerAutomationEntries()
    {
        if (_data is null || _state.Mode != ViewMode.WorldOverview ||
            _hiddenCategories.Contains(PlacedObjectCategory.MapMarker) ||
            MapCanvas.ActualWidth < 1 || MapCanvas.ActualHeight < 1)
        {
            return [];
        }

        var profile = GameProfiles.For(_data.Game);
        var metrics = MapMarkerMetrics.Resolve(profile, _zoom);
        var entries = new List<MapMarkerAutomationEntry>();
        var icons = _markerIconSet?.Icons;

        foreach (var marker in _state.FilteredMarkers)
        {
            var rawType = marker.MarkerType.HasValue ? (int)marker.MarkerType.Value : 0;
            var iconAspect = 1f;
            if (icons?.TryGetValue(rawType, out var icon) == true && icon.SizeInPixels.Height > 0)
            {
                iconAspect = (float)icon.SizeInPixels.Width / (float)icon.SizeInPixels.Height;
            }

            var bounds = MapMarkerAutomationLayout.ResolveCanvasBounds(
                marker.X, marker.Y, _zoom, _panOffset,
                (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight,
                metrics, iconAspect, hasIcon: icons?.ContainsKey(rawType) == true);
            if (bounds is not { } visibleBounds)
            {
                continue;
            }

            var catalog = MapMarkerCatalog.Resolve(_data.Game, rawType);
            var name = string.IsNullOrWhiteSpace(marker.MarkerName)
                ? catalog.DisplayName
                : marker.MarkerName!;
            entries.Add(new MapMarkerAutomationEntry(
                marker,
                new Rect(visibleBounds.X, visibleBounds.Y, visibleBounds.Width, visibleBounds.Height),
                name,
                catalog.DisplayName));
        }

        return entries;
    }

    internal void InvokeMarkerFromAutomation(PlacedReference marker)
    {
        if (_data is null || _state.Mode != ViewMode.WorldOverview ||
            !_state.FilteredMarkers.Contains(marker) ||
            _hiddenCategories.Contains(PlacedObjectCategory.MapMarker))
        {
            return;
        }

        _state.SelectObject(marker);
        MapCanvas.Invalidate();
        InspectObject?.Invoke(this, marker);
    }

    internal Point GetMapCanvasOffsetForAutomation() =>
        MapCanvas.TransformToVisual(this).TransformPoint(default);
}

internal readonly record struct MapMarkerAutomationEntry(
    PlacedReference Marker,
    Rect CanvasBounds,
    string Name,
    string TypeName);

internal sealed class WorldMapControlAutomationPeer(WorldMapControl owner)
    : FrameworkElementAutomationPeer(owner)
{
    private readonly Dictionary<MapMarkerAutomationKey, MapMarkerAutomationPeer> _markerPeers = [];

    protected override string GetClassNameCore() => nameof(WorldMapControl);

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Pane;

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        var children = base.GetChildrenCore()?.ToList() ?? [];
        var activeKeys = new HashSet<MapMarkerAutomationKey>();

        foreach (var entry in owner.GetVisibleMarkerAutomationEntries())
        {
            var key = MapMarkerAutomationKey.From(entry.Marker);
            activeKeys.Add(key);
            if (!_markerPeers.TryGetValue(key, out var peer))
            {
                peer = new MapMarkerAutomationPeer(this, owner, entry);
                _markerPeers.Add(key, peer);
            }
            else
            {
                peer.Update(entry);
            }

            children.Add(peer);
        }

        foreach (var stale in _markerPeers.Keys.Where(k => !activeKeys.Contains(k)).ToArray())
        {
            _markerPeers.Remove(stale);
        }

        return children;
    }

    internal Rect ToScreenBounds(Rect canvasBounds)
    {
        var ownerBounds = GetBoundingRectangle();
        if (ownerBounds.Width <= 0 || ownerBounds.Height <= 0)
        {
            return default;
        }

        try
        {
            var offset = owner.GetMapCanvasOffsetForAutomation();
            var scale = owner.XamlRoot?.RasterizationScale ?? 1d;
            return new Rect(
                ownerBounds.X + (offset.X + canvasBounds.X) * scale,
                ownerBounds.Y + (offset.Y + canvasBounds.Y) * scale,
                canvasBounds.Width * scale,
                canvasBounds.Height * scale);
        }
        catch (InvalidOperationException)
        {
            // The peer can outlive an unload/reparent transition. An empty bound tells UIA that the
            // virtual item is temporarily offscreen without taking down the automation tree.
            return default;
        }
    }
}

internal readonly record struct MapMarkerAutomationKey(
    uint FormId,
    int XBits,
    int YBits,
    int MarkerType)
{
    internal static MapMarkerAutomationKey From(PlacedReference marker) => new(
        marker.FormId,
        BitConverter.SingleToInt32Bits(marker.X),
        BitConverter.SingleToInt32Bits(marker.Y),
        marker.MarkerType.HasValue ? (int)marker.MarkerType.Value : 0);
}

internal sealed class MapMarkerAutomationPeer(
    WorldMapControlAutomationPeer parent,
    WorldMapControl owner,
    MapMarkerAutomationEntry entry)
    : AutomationPeer, IInvokeProvider
{
    private MapMarkerAutomationEntry _entry = entry;

    internal void Update(MapMarkerAutomationEntry value) => _entry = value;

    protected override string GetClassNameCore() => "WorldMapMarker";

    protected override string GetNameCore() => _entry.Name;

    protected override string GetAutomationIdCore() =>
        $"WorldMapMarker-{_entry.Marker.FormId:X8}-{_entry.Marker.X:R}-{_entry.Marker.Y:R}";

    protected override string GetHelpTextCore() =>
        $"{_entry.TypeName} at ({_entry.Marker.X:F0}, {_entry.Marker.Y:F0})";

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Button;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : null;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override Rect GetBoundingRectangleCore() => parent.ToScreenBounds(_entry.CanvasBounds);

    protected override Point GetClickablePointCore()
    {
        var bounds = GetBoundingRectangleCore();
        return bounds.Width > 0 && bounds.Height > 0
            ? new Point(bounds.X + bounds.Width * 0.5, bounds.Y + bounds.Height * 0.5)
            : new Point(double.NaN, double.NaN);
    }

    protected override bool IsOffscreenCore()
    {
        var bounds = GetBoundingRectangleCore();
        return bounds.Width <= 0 || bounds.Height <= 0;
    }

    public void Invoke()
    {
        var marker = _entry.Marker;
        owner.DispatcherQueue.TryEnqueue(() => owner.InvokeMarkerFromAutomation(marker));
    }
}

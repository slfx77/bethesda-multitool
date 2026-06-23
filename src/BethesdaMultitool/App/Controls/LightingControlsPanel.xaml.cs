using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BethesdaMultitool;

/// <summary>
///     Shared lighting flyout used by both the 3D viewer (<see cref="WorldView3DControl" />) and the 2D
///     world map (<see cref="WorldMapControl" />). Hosts a Lighting on/off toggle, a Fog on/off toggle,
///     a time-of-day slider, and a weather dropdown, and raises an event per change. The host owns the
///     scene state — this control is pure UI. Fog and Weather rows can be hidden per host
///     (<see cref="ShowFog" /> / <see cref="ShowWeather" />); the 2D map only drives the hillshade light
///     direction from the time-of-day slider.
/// </summary>
public sealed partial class LightingControlsPanel : UserControl
{
    private bool _suppressWeatherSelectionEvent;

    public LightingControlsPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the Lighting toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? LightingToggled;

    /// <summary>Raised when the Fog toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? FogToggled;

    /// <summary>Raised when the time-of-day slider changes. Argument is the new game hour (0–24).</summary>
    public event EventHandler<double>? TimeChanged;

    /// <summary>Raised when the weather selection changes (suppressed during programmatic population).</summary>
    public event EventHandler<WeatherSelection>? WeatherChanged;

    /// <summary>Lighting on/off. Setting this updates the toggle (and fires <see cref="LightingToggled" />).</summary>
    public bool LightingEnabled
    {
        get => LightingToggle.IsOn;
        set => LightingToggle.IsOn = value;
    }

    /// <summary>Fog on/off. Setting this updates the toggle (and fires <see cref="FogToggled" />).</summary>
    public bool FogEnabled
    {
        get => FogToggle.IsOn;
        set => FogToggle.IsOn = value;
    }

    /// <summary>Time of day (0–24h).</summary>
    public double GameHour
    {
        get => TimeSlider.Value;
        set => TimeSlider.Value = value;
    }

    /// <summary>Whether the Fog toggle row is shown (default true; the 2D map hides it).</summary>
    public bool ShowFog
    {
        get => FogToggle.Visibility == Visibility.Visible;
        set => FogToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The current weather selection (concrete weather, or the climate-default sentinel). Lets
    /// the host re-resolve the climate default after a worldspace switch without a change event.</summary>
    public WeatherSelection CurrentWeatherSelection =>
        ToSelection(WeatherComboBox.SelectedItem as WeatherDropdownItem);

    /// <summary>Whether the Weather header + dropdown are shown (default true).</summary>
    public bool ShowWeather
    {
        get => WeatherComboBox.Visibility == Visibility.Visible;
        set
        {
            var vis = value ? Visibility.Visible : Visibility.Collapsed;
            WeatherComboBox.Visibility = vis;
            WeatherHeader.Visibility = vis;
        }
    }

    /// <summary>
    ///     Populates the weather dropdown with a "(Climate default)" entry followed by every weather,
    ///     and selects the climate-default entry. Suppresses the change event during population.
    /// </summary>
    public void SetWeathers(IReadOnlyList<WeatherRecord> weathers)
    {
        var items = new List<WeatherDropdownItem> { new("(Climate default)", null, true) };
        foreach (var w in weathers)
        {
            items.Add(new WeatherDropdownItem(WeatherLabel(w), w, false));
        }

        _suppressWeatherSelectionEvent = true;
        WeatherComboBox.ItemsSource = items;
        WeatherComboBox.SelectedIndex = 0;
        _suppressWeatherSelectionEvent = false;
    }

    private void LightingToggle_Changed(object sender, RoutedEventArgs e) =>
        LightingToggled?.Invoke(this, LightingToggle.IsOn);

    private void FogToggle_Changed(object sender, RoutedEventArgs e) =>
        FogToggled?.Invoke(this, FogToggle.IsOn);

    private void TimeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        TimeChanged?.Invoke(this, e.NewValue);

    private void WeatherComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWeatherSelectionEvent) return;
        WeatherChanged?.Invoke(this, ToSelection(WeatherComboBox.SelectedItem as WeatherDropdownItem));
    }

    /// <summary>Maps a dropdown item to a <see cref="WeatherSelection" /> (null item → no selection;
    /// climate-default sentinel → null weather resolved per-worldspace by the host).</summary>
    private static WeatherSelection ToSelection(WeatherDropdownItem? item)
    {
        if (item is null) return new WeatherSelection(null, false);
        return new WeatherSelection(item.IsClimateDefault ? null : item.Weather, item.IsClimateDefault);
    }

    private static string WeatherLabel(WeatherRecord w) =>
        string.IsNullOrEmpty(w.EditorId) ? $"0x{w.FormId:X8}" : $"{w.EditorId} (0x{w.FormId:X8})";

    /// <summary>One weather dropdown entry. The first item is the climate-default sentinel
    /// (<see cref="IsClimateDefault" /> true, <see cref="Weather" /> null); the rest carry a concrete
    /// weather. <see cref="ToString" /> drives the display.</summary>
    private sealed record WeatherDropdownItem(string Label, WeatherRecord? Weather, bool IsClimateDefault)
    {
        public override string ToString() => Label;
    }
}

/// <summary>A weather selection from <see cref="LightingControlsPanel" />: either a concrete weather, or
/// the climate-default sentinel (<see cref="IsClimateDefault" /> true, resolved per-worldspace by the host).</summary>
public readonly record struct WeatherSelection(WeatherRecord? Weather, bool IsClimateDefault);

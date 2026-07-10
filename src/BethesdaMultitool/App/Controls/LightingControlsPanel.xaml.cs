using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace BethesdaMultitool;

/// <summary>
///     Shared lighting flyout used by both the 3D viewer (<see cref="WorldView3DControl" />) and the 2D
///     world map (<see cref="WorldMapControl" />). The flyout has two sections — "Lighting" (lighting /
///     fog / shadows / skybox toggles + time-of-day slider with stepper + lunar day) and "Weather"
///     (weather dropdown + wind override row) — and raises an event per change. The host owns the scene
///     state — this control is pure UI. Rows can be hidden per host (<see cref="ShowFog" /> /
///     <see cref="ShowWeather" /> / <see cref="ShowSkybox" /> …); the 2D map only drives the hillshade
///     light direction from the time-of-day slider.
/// </summary>
public sealed partial class LightingControlsPanel : UserControl
{
    private bool _suppressWeatherSelectionEvent;
    private bool _suppressTimeSync;
    private bool _suppressWindEvents;

    public LightingControlsPanel()
    {
        InitializeComponent();
        UpdateTimeValueText();
        UpdateDayValueText();
        UpdateShadowDistanceValueText(
            ShadowDistanceSlider.Value >= ShadowDistanceSlider.Maximum, ShadowDistanceSlider.Value);
    }

    /// <summary>Raised when the Lighting toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? LightingToggled;

    /// <summary>Raised when the Fog toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? FogToggled;

    /// <summary>Raised when the Sun-shadows toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? ShadowsToggled;

    /// <summary>Raised when the Shadow-distance slider changes. Argument is the coverage radius in
    /// CELLS, or <see cref="double.PositiveInfinity" /> when the slider sits at its "Unlimited"
    /// maximum (coverage then follows the render distance).</summary>
    public event EventHandler<double>? ShadowDistanceChanged;

    /// <summary>Raised when the Skybox toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? SkyboxToggled;

    /// <summary>Raised when the "Override wind" checkbox changes. Argument is the new state
    /// (true = the host should use <see cref="WindSpeed" />; false = follow the weather).</summary>
    public event EventHandler<bool>? WindOverrideChanged;

    /// <summary>Raised when the user moves the wind-speed slider while the override is on
    /// (programmatic display updates via <see cref="SetWindSpeedDisplay" /> do not raise it).</summary>
    public event EventHandler<double>? WindSpeedChanged;

    /// <summary>Raised when the time-of-day slider changes. Argument is the new game hour (0–24).</summary>
    public event EventHandler<double>? TimeChanged;

    /// <summary>Raised when the day slider changes. Argument is the new day of the lunar cycle (drives the
    /// moon phase + sky position in the 3D viewer).</summary>
    public event EventHandler<double>? DayChanged;

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

    /// <summary>Sun shadows on/off. Setting this updates the toggle (and fires <see cref="ShadowsToggled" />).</summary>
    public bool ShadowsEnabled
    {
        get => ShadowsToggle.IsOn;
        set => ShadowsToggle.IsOn = value;
    }

    /// <summary>Skybox on/off. Setting this updates the toggle (and fires <see cref="SkyboxToggled" />).</summary>
    public bool SkyboxEnabled
    {
        get => SkyboxToggle.IsOn;
        set => SkyboxToggle.IsOn = value;
    }

    /// <summary>Whether the wind override is active (the "Override wind" checkbox).</summary>
    public bool WindOverrideEnabled => WindOverrideCheck.IsChecked == true;

    /// <summary>The wind-speed slider value (engine scale, 0–1). Meaningful as an override only
    /// while <see cref="WindOverrideEnabled" /> is true.</summary>
    public double WindSpeed => WindSpeedSlider.Value;

    /// <summary>Time of day (0–24h).</summary>
    public double GameHour
    {
        get => TimeSlider.Value;
        set => TimeSlider.Value = value;
    }

    /// <summary>Day of the lunar cycle (0–24; one full Morrowind cycle). Drives the 3D moon phase + arc.</summary>
    public double GameDay
    {
        get => DaySlider.Value;
        set => DaySlider.Value = value;
    }

    /// <summary>Whether the Day header + slider are shown (default true; the 2D map hides it — the day only
    /// affects the 3D moon billboards).</summary>
    public bool ShowDay
    {
        get => DaySlider.Visibility == Visibility.Visible;
        set
        {
            var vis = value ? Visibility.Visible : Visibility.Collapsed;
            DaySlider.Visibility = vis;
            DayRow.Visibility = vis;
        }
    }

    /// <summary>Whether the Fog toggle row is shown (default true; the 2D map hides it).</summary>
    public bool ShowFog
    {
        get => FogToggle.Visibility == Visibility.Visible;
        set => FogToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the Sun-shadows toggle + distance rows are shown (default true; the 2D map
    /// hides them — the shadow pass only exists in the 3D scene).</summary>
    public bool ShowShadows
    {
        get => ShadowsToggle.Visibility == Visibility.Visible;
        set
        {
            var vis = value ? Visibility.Visible : Visibility.Collapsed;
            ShadowsToggle.Visibility = vis;
            ShadowDistanceRow.Visibility = vis;
            ShadowDistanceSlider.Visibility = vis;
        }
    }

    /// <summary>Whether the Skybox toggle row is shown (default true; the 2D map hides it —
    /// the sky only exists in the 3D scene).</summary>
    public bool ShowSkybox
    {
        get => SkyboxToggle.Visibility == Visibility.Visible;
        set => SkyboxToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The current weather selection (concrete weather, or the climate-default sentinel). Lets
    /// the host re-resolve the climate default after a worldspace switch without a change event.</summary>
    public WeatherSelection CurrentWeatherSelection =>
        ToSelection(WeatherComboBox.SelectedItem as WeatherDropdownItem);

    /// <summary>Whether the whole Weather section (separator, header, dropdown, wind row) is shown
    /// (default true; the 2D map hides it).</summary>
    public bool ShowWeather
    {
        get => WeatherComboBox.Visibility == Visibility.Visible;
        set
        {
            var vis = value ? Visibility.Visible : Visibility.Collapsed;
            WeatherSeparator.Visibility = vis;
            WeatherComboBox.Visibility = vis;
            WeatherHeader.Visibility = vis;
            WindRow.Visibility = vis;
        }
    }

    /// <summary>
    ///     Reflects the weather-driven wind speed on the (disabled) slider while the override is off,
    ///     so the row always shows the effective value. Never raises <see cref="WindSpeedChanged" />.
    /// </summary>
    public void SetWindSpeedDisplay(double windSpeed)
    {
        if (WindOverrideEnabled) return;
        _suppressWindEvents = true;
        WindSpeedSlider.Value = Math.Clamp(windSpeed, 0, 1);
        _suppressWindEvents = false;
    }

    /// <summary>
    ///     Turns the wind override on at <paramref name="windSpeed" /> without raising events — used by
    ///     the host to reflect an environment-variable override at startup (host state already set).
    /// </summary>
    public void SeedWindOverride(double windSpeed)
    {
        _suppressWindEvents = true;
        WindSpeedSlider.Value = Math.Clamp(windSpeed, 0, 1);
        WindOverrideCheck.IsChecked = true;
        WindSpeedSlider.IsEnabled = true;
        _suppressWindEvents = false;
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

    private void ShadowsToggle_Changed(object sender, RoutedEventArgs e) =>
        ShadowsToggled?.Invoke(this, ShadowsToggle.IsOn);

    private void ShadowDistanceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Parse-time early fire (markup Value="4"): read Maximum off the sender and null-guard the
        // value text, which may not be name-connected yet (same hazard as TimeStepper above).
        if (sender is not Slider slider || ShadowDistanceValueText is null) return;
        var unlimited = e.NewValue >= slider.Maximum;
        UpdateShadowDistanceValueText(unlimited, e.NewValue);
        ShadowDistanceChanged?.Invoke(this, unlimited ? double.PositiveInfinity : e.NewValue);
    }

    /// <summary>Shows the slider value ("4 cells" / "Unlimited" at the max stop).</summary>
    private void UpdateShadowDistanceValueText(bool unlimited, double cells) =>
        ShadowDistanceValueText.Text = unlimited ? "Unlimited" : $"{(int)cells} cells";

    private void SkyboxToggle_Changed(object sender, RoutedEventArgs e) =>
        SkyboxToggled?.Invoke(this, SkyboxToggle.IsOn);

    private void WindOverrideCheck_Changed(object sender, RoutedEventArgs e)
    {
        WindSpeedSlider.IsEnabled = WindOverrideCheck.IsChecked == true;
        if (_suppressWindEvents) return;
        WindOverrideChanged?.Invoke(this, WindOverrideCheck.IsChecked == true);
    }

    private void WindSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Only user input while overriding counts — SetWindSpeedDisplay's auto tracking stays silent.
        if (_suppressWindEvents || !WindOverrideEnabled) return;
        WindSpeedChanged?.Invoke(this, e.NewValue);
    }

    private void TimeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateTimeValueText();
        // Keep the stepper in lock-step (guard against the NumberBox → slider echo).
        if (!_suppressTimeSync)
        {
            _suppressTimeSync = true;
            TimeStepper.Value = Math.Round(e.NewValue, 1);
            _suppressTimeSync = false;
        }

        TimeChanged?.Invoke(this, e.NewValue);
    }

    private void TimeStepper_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressTimeSync) return;
        // NaN = the user cleared the text box; ignore until a real value arrives.
        if (double.IsNaN(args.NewValue)) return;
        // Parse-time early fire: the markup Value="12" applies while TimeSlider (declared later in
        // the XAML) is still null — a throw here surfaces as a XamlParseException on NumberBox.Value
        // and crashes the whole control. The slider's own markup Value matches, so skipping is safe.
        if (TimeSlider is null) return;
        _suppressTimeSync = true;
        TimeSlider.Value = Math.Clamp(args.NewValue, 0, 24); // fires TimeChanged via the slider
        _suppressTimeSync = false;
    }

    /// <summary>Formats the 0–24h slider value as HH:MM (24:00 wraps to 00:00).</summary>
    private void UpdateTimeValueText()
    {
        var v = TimeSlider.Value;
        var totalMinutes = (int)Math.Round(v * 60.0);
        var hours = (totalMinutes / 60) % 24;
        var minutes = totalMinutes % 60;
        TimeValueText.Text = $"{hours:00}:{minutes:00}";
    }

    private void DaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateDayValueText();
        DayChanged?.Invoke(this, e.NewValue);
    }

    /// <summary>Shows the whole-day slider value (e.g. "Day 6").</summary>
    private void UpdateDayValueText() => DayValueText.Text = $"Day {(int)DaySlider.Value}";

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

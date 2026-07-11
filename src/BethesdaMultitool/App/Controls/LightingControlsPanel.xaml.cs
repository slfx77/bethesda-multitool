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
    private bool _suppressWindEvents;

    public LightingControlsPanel()
    {
        InitializeComponent();
        UpdateTimeSegments(TimeSlider.Value);
        SelectTimeSegment(minutes: false); // hours selected by default
        UpdateDayValueText();
    }

    /// <summary>Raised when the Lighting toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? LightingToggled;

    /// <summary>Raised when the Fog toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? FogToggled;

    /// <summary>Raised when the Sun-shadows toggle changes. Argument is the new on/off state.</summary>
    public event EventHandler<bool>? ShadowsToggled;

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

    /// <summary>Whether the Sun-shadows toggle row is shown (default true; the 2D map hides it —
    /// the shadow pass only exists in the 3D scene).</summary>
    public bool ShowShadows
    {
        get => ShadowsToggle.Visibility == Visibility.Visible;
        set => ShadowsToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
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
        UpdateTimeSegments(e.NewValue);
        TimeChanged?.Invoke(this, e.NewValue);
    }

    // ---- Segmented HH:MM stepper ----------------------------------------------------------------
    // The selected segment (hours or minutes) is what the chevron RepeatButtons — and Up/Down on a
    // focused segment — step. Minutes wrap 0↔59 WITHOUT carrying into the hour (deliberate: fine
    // minute nudges must not yank the scene across an hour boundary); hours wrap 0↔23. The slider
    // stays the canonical value: stepping writes TimeSlider.Value, which raises TimeChanged and
    // refreshes the segment text, so hosts see one unified time source.

    private bool _minutesSegmentSelected;

    private void HourSegment_Click(object sender, RoutedEventArgs e) => SelectTimeSegment(minutes: false);

    private void MinuteSegment_Click(object sender, RoutedEventArgs e) => SelectTimeSegment(minutes: true);

    private void TimeUpButton_Click(object sender, RoutedEventArgs e) => StepSelectedTimeField(+1);

    private void TimeDownButton_Click(object sender, RoutedEventArgs e) => StepSelectedTimeField(-1);

    /// <summary>Up/Down on a FOCUSED segment selects and steps that segment directly (keyboard
    /// parity with click-then-chevron).</summary>
    private void TimeSegment_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var isMinutes = ReferenceEquals(sender, MinuteSegment);
        if (e.Key == Windows.System.VirtualKey.Up)
        {
            SelectTimeSegment(isMinutes);
            StepSelectedTimeField(+1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Down)
        {
            SelectTimeSegment(isMinutes);
            StepSelectedTimeField(-1);
            e.Handled = true;
        }
    }

    /// <summary>Marks the segment the chevrons drive with an accent underline (constant border
    /// thickness in markup, so selection never shifts layout).</summary>
    private void SelectTimeSegment(bool minutes)
    {
        _minutesSegmentSelected = minutes;
        if (HourSegment is null || MinuteSegment is null) return; // parse-time early fire
        var accent = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var brush)
            ? brush as Microsoft.UI.Xaml.Media.Brush
            : null;
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HourSegment.BorderBrush = minutes ? transparent : accent ?? transparent;
        MinuteSegment.BorderBrush = minutes ? accent ?? transparent : transparent;
    }

    private void StepSelectedTimeField(int direction)
    {
        if (TimeSlider is null) return; // parse-time early fire
        var totalMinutes = (int)Math.Round(TimeSlider.Value * 60.0);
        var hours = (totalMinutes / 60) % 24;
        var minutes = totalMinutes % 60;
        if (_minutesSegmentSelected)
        {
            minutes = (minutes + direction + 60) % 60; // rolls 59↔0; the hour is untouched
        }
        else
        {
            hours = (hours + direction + 24) % 24;
        }

        TimeSlider.Value = hours + (minutes / 60.0); // fires TimeChanged + UpdateTimeSegments
    }

    /// <summary>Reflects an hour value (0–24 fractional; 24:00 wraps to 00:00) on the HH / MM
    /// segment faces.</summary>
    private void UpdateTimeSegments(double hourValue)
    {
        if (HourSegment is null || MinuteSegment is null) return; // parse-time early fire
        var totalMinutes = (int)Math.Round(hourValue * 60.0);
        var hours = (totalMinutes / 60) % 24;
        var minutes = totalMinutes % 60;
        HourSegment.Content = $"{hours:00}";
        MinuteSegment.Content = $"{minutes:00}";
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

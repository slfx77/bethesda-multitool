using Windows.Graphics;
using Windows.UI;
using FalloutXbox360Utils;
using FalloutXbox360Utils.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutMap2DProfiler;

internal sealed class MainWindow : Window, IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly Map2DProfilerOptions _options;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly WorldMapControl _worldMap;
    private bool _started;
    private bool _disposed;

    public MainWindow(Map2DProfilerOptions options)
    {
        _options = options;
        Title = "Fallout Map 2D Profiler";

        _worldMap = new WorldMapControl();
        _worldMap.Loaded += OnWorldMapLoaded;

        _statusText = new TextBlock
        {
            Text = "Starting...",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis
        };

        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top
        };

        Content = BuildLayout();
        Closed += OnClosed;
        ConfigureWindow();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _worldMap.Dispose();
        GC.SuppressFinalize(this);
    }

    private Grid BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        Grid.SetRow(_worldMap, 0);
        root.Children.Add(_worldMap);

        var statusPanel = new Grid
        {
            MinHeight = 28,
            Padding = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x20, 0x20, 0x20)),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        statusPanel.Children.Add(_progressBar);
        Grid.SetRow(_statusText, 1);
        statusPanel.Children.Add(_statusText);

        Grid.SetRow(statusPanel, 1);
        root.Children.Add(statusPanel);
        return root;
    }

    private void ConfigureWindow()
    {
        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(_options.WindowWidth, _options.WindowHeight));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (displayArea is not null)
        {
            appWindow.Move(new PointInt32(
                Math.Max(0, (displayArea.WorkArea.Width - appWindow.Size.Width) / 2),
                Math.Max(0, (displayArea.WorkArea.Height - appWindow.Size.Height) / 2)));
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Dispose();
    }

    private async void OnWorldMapLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            SetStatus("Loading map data...");
            var progress = new Progress<string>(message =>
            {
                SetStatus(message);
                Log.Info(message);
            });

            var data = await Task.Run(async () =>
                await Map2DProfilerDataLoader.LoadAsync(_options, progress));

            SetStatus("Loading data into WorldMapControl...");
            _worldMap.LoadData(data);
            _progressBar.IsIndeterminate = false;
            _progressBar.Visibility = Visibility.Collapsed;

            var summary = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Profiling {0:N0} cells, {1:N0} worldspace(s). Log: {2}",
                data.AllCells.Count,
                data.Worldspaces.Count,
                _options.ProfileOutputPath);
            SetStatus(summary);
            Log.Info(summary);

            if (_options.WorldspaceIndex is int wsIdx)
            {
                if (wsIdx >= 0 && wsIdx < _worldMap.Profiler_WorldspaceCount)
                {
                    _worldMap.Profiler_WorldspaceSelectedIndex = wsIdx;
                    Log.Info("Profiler: selected worldspace index {0}.", wsIdx);
                }
                else
                {
                    Log.Warn("Profiler: --worldspace {0} out of range [0,{1}).",
                        wsIdx, _worldMap.Profiler_WorldspaceCount);
                }
            }

            StartScenarioIfRequested();
            StartTimedExitIfRequested();
        }
        catch (Exception ex)
        {
            _progressBar.IsIndeterminate = false;
            SetStatus($"Failed: {ex.GetType().Name}: {ex.Message}");
            Log.Error("Map 2D profiler startup failed: {0}", ex);
        }
    }

    private void StartScenarioIfRequested()
    {
        if (string.IsNullOrWhiteSpace(_options.ScenarioName))
        {
            return;
        }

        var scenario = Map2DScenario.Resolve(_options.ScenarioName);
        if (scenario is null)
        {
            Log.Warn("Profiler: unknown scenario '{0}'. Skipping.", _options.ScenarioName);
            return;
        }

        Log.Info("Profiler: starting scenario '{0}'.", _options.ScenarioName);
        _ = scenario.RunAsync(_worldMap, DispatcherQueue);
    }

    private void StartTimedExitIfRequested()
    {
        if (_options.DurationSeconds is not { } seconds)
        {
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            Log.Info("FalloutMap2DProfiler: duration elapsed ({0}s); exiting.", seconds);
            Dispose();
            Application.Current.Exit();
        };
        timer.Start();
        Log.Info("FalloutMap2DProfiler: timed exit armed for {0}s.", seconds);
    }

    private void SetStatus(string message)
    {
        _statusText.Text = message;
    }
}

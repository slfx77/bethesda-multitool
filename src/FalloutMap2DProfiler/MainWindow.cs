using System.Globalization;
using Windows.Graphics;
using Windows.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutMap2DProfiler;

internal sealed class MainWindow : Window, IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly Map2DProfilerOptions _options;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly WorldMapControl _worldMap;
    private readonly WorldView3DControl? _worldView3D;
    private bool _disposed;
    private bool _started;

    public MainWindow(Map2DProfilerOptions options)
    {
        _options = options;
        Title = "Fallout Map 2D Profiler";

        _worldMap = new WorldMapControl();
        _worldMap.Loaded += OnWorldMapLoaded;

        // FALLOUT_PROFILER_WITH_3D=1 replicates SingleFileTab's 2D+3D coupling: both controls share
        // one WorldViewData and their terrain paths run concurrently. That coupling is the regime
        // that triggers the WastelandNV-scale heap corruption the 3D-only profiler can't reproduce.
        // --rendered-models implies the same coupling AND turns on the top-down overlay so the run is
        // a true full-path perf test. Gated so the existing 2D-only scenarios are unaffected by default.
        if (EnvironmentVariables.IsEnabled(EnvironmentVariables.Profiler.With3D) || _options.RenderedModels)
        {
            _worldView3D = new WorldView3DControl();
        }

        _statusText = new TextBlock
        {
            Text = "Starting...",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
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
        _worldView3D?.Dispose();
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

        if (_worldView3D is not null)
        {
            // Collapsed but in the visual tree, so it creates its D3D12 device + streaming pipelines
            // exactly like the GUI does while the user looks at the 2D map.
            Grid.SetRow(_worldView3D, 0);
            _worldView3D.Visibility = Visibility.Collapsed;
            root.Children.Add(_worldView3D);
        }

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
            if (_worldView3D is not null)
            {
                try
                {
                    // Mirror SingleFileTab.WorldMap.cs: load the SAME data into the 3D control and wire
                    // it as the 2D map's top-down provider. This brings up the 3D streaming concurrently.
                    _worldView3D.LoadData(data);
                    _worldMap.TopDownProvider = _worldView3D;
                    Log.Info("Profiler: 3D control loaded + wired — 2D+3D coupling active.");

                    if (_options.RenderedModels)
                    {
                        // The overlay lives on the TerrainTextures layer (where the mip/perf + water work
                        // is); a scenario may re-select it, which is idempotent. Then enable the overlay
                        // once the 3D provider's D3D12 backend + terrain/reference pipelines are up.
                        _worldMap.Profiler_Layer = WorldMapLayer.TerrainTextures;
                        EnableRenderedModelsWhenReady();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Profiler: 3D control init failed: {0}", ex);
                }
            }

            _progressBar.IsIndeterminate = false;
            _progressBar.Visibility = Visibility.Collapsed;

            var summary = string.Format(
                CultureInfo.InvariantCulture,
                "Profiling {0:N0} cells, {1:N0} worldspace(s). Log: {2}",
                data.AllCells.Count,
                data.Worldspaces.Count,
                _options.ProfileOutputPath);
            SetStatus(summary);
            Log.Info(summary);

            // Log the worldspace list so a profiling run can pick the right --worldspace index.
            var labels = _worldMap.Profiler_WorldspaceLabels;
            for (var i = 0; i < labels.Count; i++)
            {
                Log.Info("Profiler: worldspace[{0}] = {1}", i, labels[i]);
            }

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

    /// <summary>
    ///     Polls the 3D provider until its D3D12 backend + terrain/reference pipelines report ready
    ///     (<see cref="ITopDownSceneRenderer.CanRenderTopDown" />), then enables the rendered-models
    ///     overlay. The overlay gate in WorldMapControl ignores an enable issued before the provider is
    ///     ready, so the poll is the robust way to turn it on autonomously. Bounded (~30s) so a provider
    ///     that never comes up (no Meshes BSA / D3D12 down) doesn't poll forever.
    /// </summary>
    private void EnableRenderedModelsWhenReady()
    {
        if (_worldView3D is not ITopDownSceneRenderer provider)
        {
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = true;
        var attempts = 0;
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (provider.CanRenderTopDown)
            {
                _worldMap.Profiler_ShowRenderedObjects = true;
                timer.Stop();
                Log.Info("Profiler: rendered-models overlay enabled (provider ready after {0} poll(s)).", attempts);
            }
            else if (attempts >= 120)
            {
                timer.Stop();
                Log.Warn(
                    "Profiler: 3D provider never became ready after {0} polls; rendered-models overlay NOT enabled.",
                    attempts);
            }
        };
        timer.Start();
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

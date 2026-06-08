using Windows.Graphics;
using Windows.UI;
using FalloutXbox360Utils;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutRendererProfiler;

internal sealed class MainWindow : Window, IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly RendererProfilerOptions _options;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    private readonly WorldView3DControl _worldView;
    private Renderer3DScenario? _scenario;
    private DispatcherQueueTimer? _timedExitTimer;
    private bool _started;
    private bool _exiting;
    private bool _disposed;
    private bool _traceClosed;

    public MainWindow(RendererProfilerOptions options)
    {
        _options = options;
        Title = "Fallout Renderer Profiler";

        _worldView = new WorldView3DControl();
        _worldView.Loaded += OnWorldViewLoaded;

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
        _timedExitTimer?.Stop();
        _timedExitTimer = null;
        _scenario?.Dispose();
        _scenario = null;
        _worldView.Dispose();
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

        Grid.SetRow(_worldView, 0);
        root.Children.Add(_worldView);

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
        CloseProfilerTrace(_exiting ? "timed-exit" : "window-closed");
        Log.CloseLogFile();
    }

    private async void OnWorldViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            SetStatus("Loading renderer data...");
            var progress = new Progress<string>(message =>
            {
                SetStatus(message);
                Log.Info(message);
            });

            var data = await Task.Run(async () =>
                await RendererProfilerDataLoader.LoadAsync(_options, progress));

            SetStatus("Opening 3D viewer...");
            _worldView.LoadData(data);
            _progressBar.IsIndeterminate = false;
            _progressBar.Visibility = Visibility.Collapsed;

            var summary = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Profiling {0:N0} cells, {1:N0} worldspace(s), {2:N0} placed refs. Log: {3}",
                data.AllCells.Count,
                data.Worldspaces.Count,
                data.RefrToCellIndex.Count,
                _options.ProfileOutputPath);
            SetStatus(summary);
            Log.Info(summary);
            _scenario = Renderer3DScenario.Start(_worldView, DispatcherQueue, _options);

            StartTimedExitIfRequested();
        }
        catch (Exception ex)
        {
            _progressBar.IsIndeterminate = false;
            SetStatus($"Failed: {ex.GetType().Name}: {ex.Message}");
            Log.Error("Renderer profiler startup failed: {0}", ex);
        }
    }

    private void StartTimedExitIfRequested()
    {
        if (_options.DurationSeconds is not { } seconds)
        {
            return;
        }

        _timedExitTimer?.Stop();
        _timedExitTimer = DispatcherQueue.CreateTimer();
        _timedExitTimer.Interval = TimeSpan.FromSeconds(seconds);
        _timedExitTimer.IsRepeating = false;
        _timedExitTimer.Tick += (_, _) =>
        {
            ExitProfiler(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "FalloutRendererProfiler: duration elapsed ({0}s); exiting.",
                seconds));
        };
        _timedExitTimer.Start();
        Log.Info("FalloutRendererProfiler: timed exit armed for {0}s.", seconds);
    }

    private void ExitProfiler(string message)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        SetStatus(message);
        Log.Info(message);
        Console.WriteLine($"[FalloutRendererProfiler] Complete. Profile log: {_options.ProfileOutputPath}");
        _timedExitTimer?.Stop();
        _timedExitTimer = null;
        Dispose();
        CloseProfilerTrace("duration-elapsed");
        Log.CloseLogFile();
        Application.Current.Exit();
    }

    private void CloseProfilerTrace(string reason)
    {
        if (_traceClosed)
        {
            return;
        }

        _traceClosed = true;
        RendererProfilerTrace.Event("shutdown", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["profileOutput"] = _options.ProfileOutputPath,
            ["profileJsonl"] = _options.ProfileJsonlOutputPath
        });
        RendererProfilerTrace.Close();
    }

    private void SetStatus(string message)
    {
        _statusText.Text = message;
    }
}

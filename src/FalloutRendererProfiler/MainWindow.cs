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

            if (!string.IsNullOrWhiteSpace(_options.CaptureTopDownPath))
            {
                // Autonomous top-down overlay capture: render the 2D-map "Rendered models" overlay
                // for a window around the camera, save a PNG + log coverage, then exit. No live
                // scenario / timed exit in this mode.
                _ = RunTopDownCaptureAsync();
                return;
            }

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

    private async Task RunTopDownCaptureAsync()
    {
        var path = _options.CaptureTopDownPath!;
        try
        {
            if (_worldView is not ITopDownSceneRenderer provider || !provider.CanRenderTopDown)
            {
                Log.Warn("Capture: top-down provider unavailable (D3D12 down or no Meshes BSA).");
                Console.WriteLine("[Capture] UNAVAILABLE: top-down provider not ready (no D3D12 / no Meshes BSA).");
                ExitProfiler("capture-unavailable");
                return;
            }

            // Collapse the live 3D view so its render loop idles — mirrors production (the 3D control
            // is collapsed while the 2D map shows) and avoids sharing the command recorder with live
            // frames during the offscreen capture.
            _worldView.Visibility = Visibility.Collapsed;
            await Task.Delay(800); // let the worldspace bookmark + initial cell grid settle

            // Center on a specific worldspace's centroid (to exercise the top-down worldspace sync)
            // or, by default, on the camera position. targetFormId drives the sync param so the
            // overlay renders the requested worldspace, not whichever the 3D view currently holds.
            float cx, cy;
            uint? targetFormId;
            if (_options.CaptureWorldspaceIndex is int wsIdx)
            {
                var center = _worldView.Profiler_GetWorldspaceCenter(wsIdx);
                if (center is null)
                {
                    Console.WriteLine($"[Capture] UNAVAILABLE: worldspace index {wsIdx} out of range / empty (count={_worldView.Profiler_ExteriorWorldspaceCount}).");
                    ExitProfiler("capture-bad-worldspace");
                    return;
                }
                cx = center.Value.CenterX; cy = center.Value.CenterY; targetFormId = center.Value.FormId;
                Log.Info("Capture: worldspace[{0}] '{1}' formId=0x{2:X8} center=({3:F0},{4:F0})",
                    wsIdx, center.Value.Name, center.Value.FormId, cx, cy);
            }
            else
            {
                var pose = _worldView.Profiler_CameraPose;
                cx = pose.Position.X; cy = pose.Position.Y;
                targetFormId = _worldView.Profiler_SelectedWorldspaceFormId;
            }

            var half = Math.Max(1, _options.CaptureSpanCells) * 0.5f * WorldGridConstants.CellSize;
            float minX = cx - half, maxX = cx + half, minY = cy - half, maxY = cy + half;
            const int px = 512;

            TopDownRender? render = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                render = await provider.RenderTopDownAsync(
                    minX, maxX, minY, maxY, px, px, showDisabled: true,
                    worldspaceFormId: targetFormId, CancellationToken.None);
                if (render is null)
                {
                    await Task.Delay(250);
                    continue;
                }

                Log.Info("Capture attempt {0}: {1}x{2} complete={3} coverage={4:P2}",
                    attempt, render.Width, render.Height, render.IsComplete, Coverage(render.Bgra));
                if (render.IsComplete)
                {
                    break;
                }
                await Task.Delay(300);
            }

            if (render is null)
            {
                Console.WriteLine("[Capture] FAILED: render returned null.");
                ExitProfiler("capture-null");
                return;
            }

            var rgba = BgraToRgba(render.Bgra);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            FalloutXbox360Utils.Core.Formats.Esm.Analysis.PngWriter.SaveRgba(rgba, render.Width, render.Height, path);

            var msg = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[Capture] saved {0} ({1}x{2}) coverage={3:P2} complete={4} window={5}cells center=({6:F0},{7:F0})",
                path, render.Width, render.Height, Coverage(render.Bgra), render.IsComplete,
                _options.CaptureSpanCells, cx, cy);
            Log.Info(msg);
            Console.WriteLine(msg);
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed: {0}", ex);
            Console.WriteLine($"[Capture] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ExitProfiler("capture-complete");
        }
    }

    private static double Coverage(byte[] bgra)
    {
        if (bgra.Length < 4) return 0;
        var total = bgra.Length / 4;
        long opaque = 0;
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] > 0) opaque++;
        }
        return (double)opaque / total;
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];     // R
            rgba[i + 1] = bgra[i + 1]; // G
            rgba[i + 2] = bgra[i];     // B
            rgba[i + 3] = bgra[i + 3]; // A
        }
        return rgba;
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

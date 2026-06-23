using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core;
using BethesdaMultitool;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BethesdaRendererProfiler;

internal sealed class Renderer3DScenario : IDisposable
{
    private readonly Stopwatch _clock = new();
    private readonly WorldView3DControl _control;
    private readonly RendererProfilerCameraPose _initialPose;
    private readonly RendererProfilerOptions _options;
    private readonly DispatcherQueueTimer? _timer;
    private bool _disposed;
    private long _lastMotionLogMilliseconds = -1000;

    private Renderer3DScenario(
        WorldView3DControl control,
        DispatcherQueue dispatcherQueue,
        RendererProfilerOptions options)
    {
        _control = control;
        _options = options;
        _initialPose = control.Profiler_CameraPose;
        if (options.RenderDistanceCells is { } cells)
        {
            // Bump the view distance above the bookmark default and apply it immediately so the
            // whole run (and the first frame) renders at the requested distance.
            _initialPose = _initialPose with { RenderDistance = cells * WorldGridConstants.CellSize };
            control.Profiler_SetCameraPose(_initialPose);
        }

        _control.Profiler_ClearInputState();

        RendererProfilerTrace.Event("camera-motion", BuildEventFields("start", _initialPose, 0));
        Logger.Instance.Info(
            "Renderer3DScenario: camera motion '{0}' speed={1:0.##}.",
            options.CameraMotion,
            options.CameraSpeed);

        if (options.CameraMotion != RendererCameraMotionKind.Static)
        {
            _timer = dispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.IsRepeating = true;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        _clock.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Stop();
        RendererProfilerTrace.Event(
            "camera-motion",
            BuildEventFields("stop", _control.Profiler_CameraPose, _clock.Elapsed.TotalSeconds));
    }

    internal static Renderer3DScenario Start(
        WorldView3DControl control,
        DispatcherQueue dispatcherQueue,
        RendererProfilerOptions options)
    {
        return new Renderer3DScenario(control, dispatcherQueue, options);
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        var elapsed = _clock.Elapsed.TotalSeconds;
        var pose = RendererCameraMotion.Evaluate(
            _options.CameraMotion,
            _initialPose,
            _options.CameraSpeed,
            elapsed);
        _control.Profiler_SetCameraPose(pose);

        var elapsedMs = _clock.ElapsedMilliseconds;
        if (elapsedMs - _lastMotionLogMilliseconds >= 1000)
        {
            _lastMotionLogMilliseconds = elapsedMs;
            RendererProfilerTrace.Event("camera-motion", BuildEventFields("sample", pose, elapsed));
        }
    }

    private Dictionary<string, object?> BuildEventFields(
        string phase,
        RendererProfilerCameraPose pose,
        double elapsedSeconds)
    {
        var fields = RendererProfilerTrace.CameraPoseFields(pose);
        fields["phase"] = phase;
        fields["motion"] = _options.CameraMotion.ToString();
        fields["speed"] = _options.CameraSpeed;
        fields["elapsedSeconds"] = elapsedSeconds;
        fields["frame"] = _control.Profiler_FrameIndex;
        return fields;
    }
}

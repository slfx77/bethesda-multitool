using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Rendering;

namespace BethesdaMultitool;

/// <summary>Keeps Bethesda's surface policy and device-wide fence semantics behind the shared owner.</summary>
internal sealed class BethesdaNativeViewportGraphicsLease(
    BethesdaSceneViewerGraphicsContext12.BethesdaSceneViewerGraphicsLease12 lease,
    SwapChainPanel panel) : INativeViewportGraphicsLease
{
    private GpuSwapChainSurface12? _constructionSurface;
    private RetiredResourceDisposal? _retirement;
    /// <summary>Gets the exact borrowed donor lease used by Bethesda draw and capture policy.</summary>
    internal BethesdaSceneViewerGraphicsContext12.BethesdaSceneViewerGraphicsLease12 Lease => lease;

    /// <summary>Creates and transfers an actual Bethesda swap-chain surface without letting diagnostics lose ownership.</summary>
    /// <param name="width">Allocated physical pixel width.</param>
    /// <param name="height">Allocated physical pixel height.</param>
    /// <returns>The owned policy-specific surface bound to this exact panel.</returns>
    public INativeViewportSurface CreateSurface(uint width, uint height)
    {
        if (_retirement is not null || _constructionSurface is not null)
        {
            throw new InvalidOperationException("This native graphics lease retains an unfinished retirement and cannot create another surface.");
        }
        var started = Stopwatch.GetTimestamp();
        GpuSwapChainSurface12? surface = null;
        Logger.Instance.Debug("BethesdaSceneViewer: surface/tonemap construction started size={0}x{1}.", width, height);
        try
        {
            surface = GpuSwapChainSurface12.CreateOwned(lease.Context.Gpu, panel, width, height,
                created => _constructionSurface = created) ??
                      throw new InvalidOperationException("The native Bethesda renderer could not bind its WinUI swap chain.");
            var transferred = new BethesdaNativeViewportSurface(surface);
            _constructionSurface = null;
            return transferred;
        }
        finally
        {
            try
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Logger.Instance.Info("BethesdaSceneViewer: surface/tonemap construction timing outcome={0} size={1}x{2} elapsed={3:F2} ms.",
                    surface is null ? "failed" : "ready", width, height, elapsed);
                RendererProfilerTrace.Event("bethesda-viewer-surface-construction", new Dictionary<string, object?>
                {
                    ["outcome"] = surface is null ? "failed" : "ready",
                    ["width"] = width, ["height"] = height, ["elapsedMilliseconds"] = elapsed
                });
            }
#pragma warning disable RCS1075 // Diagnostic sinks cannot invalidate established ownership or GPU retirement proof.
            catch (Exception) { /* Diagnostics cannot interrupt an already-transferred native surface. */ }
#pragma warning restore RCS1075
        }
    }

    /// <summary>Reports only a completed direct-queue fence or proven native device removal.</summary>
    /// <param name="failure">Original native failure retained for reporting, including uncertain ownership.</param>
    /// <returns>Explicit retirement evidence; an exception alone never authorizes resource release.</returns>
    public NativeViewportRetirement EstablishRetirement(out Exception? failure)
    {
        try
        {
            lease.Context.WaitForGpuIdle();
            failure = null;
            return lease.Context.IsDeviceTerminal ? NativeViewportRetirement.DeviceTerminal : NativeViewportRetirement.Drained;
        }
        catch (Exception exception)
        {
            failure = exception;
            // The existing context terminalizes the device when a wait fails. A thrown terminalization
            // cannot be mistaken for successful GPU retirement; retain the graph until explicitly proven.
            return lease.Context.IsDeviceTerminal ? NativeViewportRetirement.DeviceTerminal : NativeViewportRetirement.Uncertain;
        }
    }

    /// <summary>Releases this counted graphics lease, preserving donor partial-cleanup retry ownership.</summary>
    public void Dispose()
    {
        if (_retirement is null)
        {
            // The enclosing shared session has already proved the graphics graph retired.
            // A failed BindPanel may never have returned a surface, but registration above
            // kept its exact retry owner alive before that native operation began.
            var retirement = new RetiredResourceDisposal();
            retirement.Add(_constructionSurface, "unfinished surface construction");
            retirement.Add(lease, "graphics lease", 1);
            _retirement = retirement;
            _constructionSurface = null;
        }
        _retirement.Dispose();
    }
}

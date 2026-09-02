using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D12;

namespace BethesdaMultitool;

public sealed partial class BethesdaSceneViewerControl
{
    private readonly object _captureGate = new();
    private BethesdaSceneViewerCaptureRequest12? _captureRequest;

    /// <summary>
    ///     Captures the next native frame exactly after the live tonemap and before Present. The
    ///     returned pixels are tightly packed BGRA8 in top-to-bottom row order. This is the same
    ///     swap-chain image the panel displays; no offscreen renderer or GLB projection participates.
    /// </summary>
    internal Task<BethesdaSceneViewerFrameCapture> CaptureFrameAsync(
        CancellationToken cancellationToken = default)
    {
        VerifyUiThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<BethesdaSceneViewerFrameCapture>(cancellationToken);
        }

        if (!_isLoaded ||
            !_isPresentationActive ||
            !IsEffectivelyVisible() ||
            _renderState != BethesdaSceneViewerRenderState.Ready ||
            _scene is null ||
            _graphicsLease is null)
        {
            return Task.FromException<BethesdaSceneViewerFrameCapture>(new InvalidOperationException(
                "A native Bethesda scene must be loaded, visible, and renderer-ready before capture."));
        }

        TryEnsureSurface();
        if (_surface is null)
        {
            return Task.FromException<BethesdaSceneViewerFrameCapture>(new InvalidOperationException(
                "The native Bethesda viewer does not have a presentation surface to capture."));
        }

        var request = new BethesdaSceneViewerCaptureRequest12(cancellationToken);
        lock (_captureGate)
        {
            if (_captureRequest is not null)
            {
                request.Dispose();
                return Task.FromException<BethesdaSceneViewerFrameCapture>(new InvalidOperationException(
                    "Only one native Bethesda viewer capture may be pending at a time."));
            }

            _captureRequest = request;
        }

        request.RegisterCancellation(() => OnCaptureCancellationRequested(request));
        if (request.IsCancellationRequested)
        {
            RemoveCanceledCaptureRequest(request);
        }
        else
        {
            // A settled static scene normally has no CompositionTarget subscription. One invalidated
            // frame records both its ordinary presentation and this optional readback copy.
            InvalidateViewport();
        }

        return request.Task;
    }

    /// <summary>
    ///     Captures the next native frame and returns PNG bytes. PNG encoding runs off the UI thread;
    ///     alpha is forced opaque because the SwapChainPanel uses DXGI AlphaMode.Ignore.
    /// </summary>
    internal async Task<byte[]> CapturePngAsync(CancellationToken cancellationToken = default)
    {
        var capture = await CaptureFrameAsync(cancellationToken);
        return await Task.Run(capture.EncodeOpaquePng, cancellationToken);
    }

    /// <summary>
    ///     Allocates the one-shot readback before any back-buffer state change. Allocation failure is
    ///     local to the capture request and leaves the ordinary frame/presentation path untouched.
    /// </summary>
    private BethesdaSceneViewerCaptureRequest12? TryPrepareCapture(
        BethesdaSceneViewerGraphicsContext12 graphics,
        GpuSwapChainSurface12 surface)
    {
        BethesdaSceneViewerCaptureRequest12? request;
        lock (_captureGate)
        {
            request = _captureRequest;
        }

        if (request is null)
        {
            return null;
        }

        // The request remains in the gate until its worker has waited and unmapped so a second
        // request cannot overlap it. Continuous animation/water frames must not record it twice.
        if (request.HasGpuWork)
        {
            return null;
        }

        if (request.IsCancellationRequested)
        {
            RemoveCanceledCaptureRequest(request);
            return null;
        }

        try
        {
            request.Prepare(
                graphics.Gpu.Device,
                graphics.Gpu.FrameFence,
                checked((int)surface.Width),
                checked((int)surface.Height));
            return request;
        }
        catch (Exception ex)
        {
            AbandonCaptureRequest(request, ex);
            return null;
        }
    }

    private void SubmitCaptureReadback(BethesdaSceneViewerCaptureRequest12 request, ulong fenceValue)
    {
        request.MarkSubmitted();

        // Do not wait on the shared recorder or the WinUI thread. The request owns an AddRef'd fence
        // interface and its readback resource, so a tab unload/control disposal can safely release the
        // graphics lease while this bounded worker finishes. Every exception is observed here.
        _ = Task.Run(() =>
        {
            try
            {
                request.WaitForFence(fenceValue);
                if (!request.IsCancellationRequested)
                {
                    request.TrySetResult(new BethesdaSceneViewerFrameCapture(
                        request.ReadbackToBytes(),
                        request.PixelWidth,
                        request.PixelHeight));
                }
            }
            catch (OperationCanceledException)
            {
                request.TrySetCanceled();
            }
            catch (Exception ex)
            {
                request.TrySetException(ex);
            }
            finally
            {
                try
                {
                    request.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("BethesdaSceneViewer: capture readback release failed: {0}", ex.Message);
                }
                finally
                {
                    lock (_captureGate)
                    {
                        if (ReferenceEquals(_captureRequest, request))
                        {
                            _captureRequest = null;
                        }
                    }
                }
            }
        });
    }

    private void OnCaptureCancellationRequested(BethesdaSceneViewerCaptureRequest12 request)
    {
        request.TrySetCanceled();

        // CancellationToken callbacks may run on any thread. Resource removal is queued to WinUI so
        // it cannot race the UI thread changing the request from pending to GPU-owned while recording.
        // Always queue, even when cancellation originated on WinUI: CancellationToken.Register may
        // invoke synchronously before it has returned the registration that request.Dispose owns.
        _ = DispatcherQueue.TryEnqueue(() => RemoveCanceledCaptureRequest(request));
    }

    private void RemoveCanceledCaptureRequest(BethesdaSceneViewerCaptureRequest12 request)
    {
        var dispose = false;
        lock (_captureGate)
        {
            if (ReferenceEquals(_captureRequest, request) && !request.HasGpuWork)
            {
                _captureRequest = null;
                dispose = true;
            }
        }

        if (dispose)
        {
            request.Dispose();
        }
    }

    private void AbandonCaptureRequest(BethesdaSceneViewerCaptureRequest12 request, Exception exception)
    {
        lock (_captureGate)
        {
            if (ReferenceEquals(_captureRequest, request))
            {
                _captureRequest = null;
            }
        }

        request.TrySetException(exception);
        request.Dispose();
    }

    /// <summary>
    ///     Completes a capture whose copy may be executing but has no usable completion fence. The
    ///     recorder received ownership while the frame gate was still held and releases the request
    ///     only during its final idle/device teardown; disposing it here would race the GPU.
    /// </summary>
    private void FailUnfencedCaptureRequest(
        BethesdaSceneViewerCaptureRequest12 request,
        Exception exception)
    {
        lock (_captureGate)
        {
            if (ReferenceEquals(_captureRequest, request))
            {
                _captureRequest = null;
            }
        }

        request.TrySetException(exception);
    }

    /// <summary>Fails a request that has not entered a submitted GPU frame. Submitted copies retain
    /// their fence/readback ownership until the worker observes completion.</summary>
    private void CancelPendingCapture(string reason)
    {
        BethesdaSceneViewerCaptureRequest12? pending = null;
        lock (_captureGate)
        {
            if (_captureRequest is { HasGpuWork: false } request)
            {
                _captureRequest = null;
                pending = request;
            }
        }

        if (pending is not null)
        {
            pending.TrySetException(new InvalidOperationException(reason));
            pending.Dispose();
        }
    }

    private void CancelCaptureForControlDisposal()
    {
        BethesdaSceneViewerCaptureRequest12? pending = null;
        lock (_captureGate)
        {
            var request = _captureRequest;
            if (request is null)
            {
                return;
            }

            request.TrySetException(new ObjectDisposedException(nameof(BethesdaSceneViewerControl)));
            if (!request.HasGpuWork)
            {
                _captureRequest = null;
                pending = request;
            }
        }

        pending?.Dispose();
    }

    private sealed unsafe class BethesdaSceneViewerCaptureRequest12 : IDisposable
    {
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<BethesdaSceneViewerFrameCapture> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _cancellationRegistration;
        private ID3D12Fence? _fence;
        private ID3D12Resource? _readback;
        private PlacedSubresourceFootPrint _readbackFootprint;
        private uint _readbackRowPitch;
        private int _disposed;

        internal BethesdaSceneViewerCaptureRequest12(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        internal Task<BethesdaSceneViewerFrameCapture> Task => _completion.Task;

        internal bool IsCancellationRequested => _cancellationToken.IsCancellationRequested;

        internal bool HasGpuWork { get; private set; }

        internal int PixelWidth { get; private set; }

        internal int PixelHeight { get; private set; }

        internal void RegisterCancellation(Action callback)
        {
            _cancellationRegistration = _cancellationToken.Register(callback);
        }

        internal void Prepare(
            ID3D12Device device,
            ID3D12Fence frameFence,
            int pixelWidth,
            int pixelHeight)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (HasGpuWork)
            {
                throw new InvalidOperationException("The native viewer capture was prepared twice.");
            }

            var copyDesc = ResourceDescription.Texture2D(
                GpuSwapChainSurface12.BackBufferFormat,
                checked((uint)pixelWidth),
                checked((uint)pixelHeight),
                1,
                1);
            var footprints = new PlacedSubresourceFootPrint[1];
            var rowCounts = new uint[1];
            var rowSizes = new ulong[1];
            device.GetCopyableFootprints(
                copyDesc,
                0,
                1,
                0,
                footprints,
                rowCounts,
                rowSizes,
                out var readbackBytes);

            ID3D12Fence? ownedFence = null;
            ID3D12Resource? readback = null;
            try
            {
                // Independent COM references make the worker safe if the control releases the last
                // shared graphics-context lease immediately after this frame is submitted.
                ownedFence = frameFence.QueryInterface<ID3D12Fence>();
                readback = device.CreateCommittedResource<ID3D12Resource>(
                    HeapProperties.ReadbackHeapProperties,
                    HeapFlags.None,
                    ResourceDescription.Buffer(readbackBytes),
                    ResourceStates.CopyDest);
                readback.Name = "Bethesda Scene Viewer One-Shot BGRA8 Readback";

                _fence = ownedFence;
                _readback = readback;
                _readbackFootprint = footprints[0];
                _readbackRowPitch = footprints[0].Footprint.RowPitch;
                PixelWidth = pixelWidth;
                PixelHeight = pixelHeight;
                HasGpuWork = true;
            }
            catch
            {
                readback?.Dispose();
                ownedFence?.Dispose();
                throw;
            }
        }

        internal void RecordCopy(ID3D12GraphicsCommandList commandList, ID3D12Resource backBuffer)
        {
            if (_readback is null || !HasGpuWork)
            {
                throw new InvalidOperationException("The native viewer capture has no prepared readback resource.");
            }

            // ResolveTo has already written the final tonemapped LDR pixels and left this exact
            // B8G8R8A8_UNorm swap-chain resource in RENDER_TARGET. The copy is part of the same
            // command list, and the final barrier supplies the Present baseline directly.
            commandList.ResourceBarrierTransition(
                backBuffer,
                ResourceStates.RenderTarget,
                ResourceStates.CopySource);
            commandList.CopyTextureRegion(
                new TextureCopyLocation(_readback, _readbackFootprint),
                0,
                0,
                0,
                new TextureCopyLocation(backBuffer));
            commandList.ResourceBarrierTransition(
                backBuffer,
                ResourceStates.CopySource,
                ResourceStates.Present);
        }

        internal void MarkSubmitted()
        {
            if (!HasGpuWork)
            {
                throw new InvalidOperationException("A native viewer capture cannot submit before preparation.");
            }
        }

        internal void WaitForFence(ulong fenceValue)
        {
            var fence = _fence ??
                        throw new InvalidOperationException("The native viewer capture has no frame fence.");
            using var waitEvent = new AutoResetEvent(false);
            D3D12FenceWaiter.WaitForFence(fence, fenceValue, waitEvent);
            _cancellationToken.ThrowIfCancellationRequested();
        }

        internal byte[] ReadbackToBytes()
        {
            var readback = _readback ??
                           throw new InvalidOperationException("The native viewer capture has no readback resource.");
            void* cpuPointer = null;
            readback.Map(0, &cpuPointer).CheckError();
            try
            {
                var rowBytes = checked(PixelWidth * 4);
                var pixels = new byte[checked(rowBytes * PixelHeight)];
                for (var y = 0; y < PixelHeight; y++)
                {
                    Marshal.Copy(
                        (nint)cpuPointer + checked((int)(y * _readbackRowPitch)),
                        pixels,
                        y * rowBytes,
                        rowBytes);
                }

                return pixels;
            }
            finally
            {
                readback.Unmap(0);
            }
        }

        internal void TrySetResult(BethesdaSceneViewerFrameCapture capture) =>
            _completion.TrySetResult(capture);

        internal void TrySetCanceled() =>
            _completion.TrySetCanceled(_cancellationToken);

        internal void TrySetException(Exception exception) =>
            _completion.TrySetException(exception);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellationRegistration.Dispose();
            _readback?.Dispose();
            _readback = null;
            _fence?.Dispose();
            _fence = null;
        }
    }
}

/// <summary>An exact, tightly packed BGRA8 snapshot of one presented native Bethesda frame.</summary>
internal sealed class BethesdaSceneViewerFrameCapture
{
    internal BethesdaSceneViewerFrameCapture(byte[] bgraPixels, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (pixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        if (bgraPixels.Length != checked(pixelWidth * pixelHeight * 4))
        {
            throw new ArgumentException("The BGRA buffer is not tightly packed for its dimensions.",
                nameof(bgraPixels));
        }

        BgraPixels = bgraPixels;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    internal byte[] BgraPixels { get; }

    internal int PixelWidth { get; }

    internal int PixelHeight { get; }

    internal byte[] EncodeOpaquePng()
    {
        var rgba = new byte[BgraPixels.Length];
        for (var i = 0; i < BgraPixels.Length; i += 4)
        {
            rgba[i] = BgraPixels[i + 2];
            rgba[i + 1] = BgraPixels[i + 1];
            rgba[i + 2] = BgraPixels[i];
            rgba[i + 3] = 255;
        }

        return PngWriter.EncodeRgba(rgba, PixelWidth, PixelHeight);
    }
}

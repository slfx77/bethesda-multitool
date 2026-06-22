using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private void UpdateHud(int visible, int total, int visibleWater, int visibleReferences, int visibleNavMesh)
    {
        if (!_hudHidden && StatusOverlay.Visibility != Visibility.Visible && HudPanel.Visibility != Visibility.Visible)
        {
            HudPanel.Visibility = Visibility.Visible;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastHudUpdateTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastHudUpdateTimestamp, now).TotalMilliseconds < HudUpdateIntervalMilliseconds)
        {
            return;
        }
        _lastHudUpdateTimestamp = now;

        var mode = _controller.Mode == CameraMode.Walk ? "walk" : "fly";
        // Time-of-day readout — the slider drives _gameHour; show it as HH:MM.
        var hour = (int)_gameHour;
        var minute = (int)((_gameHour - hour) * 60f);
        // Backend chip — D3D12 shows the feature level for at-a-glance diagnostics.
        var backend = _gpu12 is not null
            ? $"D3D12 {_gpu12.FeatureLevel}"
            : "D3D11";
        // Layer on/off state now lives in the toolbar toggle buttons, so the HUD spends the
        // freed space spelling out the movement controls.
        var text =
            $"[{backend}]   " +
            $"Cells: {visible} / {total}   refs: {visibleReferences}   nav: {visibleNavMesh}   " +
            $"pos: ({_camera.Position.X:0}, {_camera.Position.Y:0}, {_camera.Position.Z:0})   " +
            $"speed: {_controller.MoveSpeed:0}   " +
            $"dist: {_renderDistance / _cellSize:0.#}c   " +
            $"mode: {mode}   time {hour:00}:{minute:00}\n" +
            "WASD move   Q/E up/down   mouse-wheel speed   drag to look   " +
            "PgUp/PgDn view distance   F fly/walk   click select (click again = cycle)   Esc deselect";

        // Draw-cap signal: when a dense frame can't fit every per-draw CB in the shared ring slot,
        // the renderer skips the overflow (instead of throwing + blanking the scene). Surface it so
        // the soft cap is visible — raise FALLOUT_VIEWER_RING_BUFFER_MB if this is persistently > 0.
        var truncatedDraws = _references?.LastFrameDrawsTruncated ?? 0;
        if (truncatedDraws > 0)
        {
            var ringTotalMib = _ringBuffer12 is not null ? _ringBuffer12.BytesPerFrame / (1024.0 * 1024.0) : 0;
            text += $"\n⚠ DRAW-CAP: {truncatedDraws} draws skipped this frame " +
                    $"(ring {ringTotalMib:0}MiB full — raise FALLOUT_VIEWER_RING_BUFFER_MB)";
        }
        if (_showFrameStats && _terrain is not null)
        {
            var stats = _terrain.LastStats;
            text +=
                $"\nstats cand:{stats.VisibleCandidates} draw:{stats.TerrainDraws} " +
                $"up:{stats.NewUploads} texMiss:{stats.TextureCacheMisses} " +
                $"water:{visibleWater} cpu:{stats.CpuFrameMilliseconds:0.0}ms";

            if (_references is not null && _showReferences)
            {
                var rstats = _references.LastStats;
                text +=
                    $"\nrefs cand:{rstats.ReferenceCandidates} drawn:{rstats.ReferenceDrawn} " +
                    $"sub:{rstats.ReferenceSubmeshDraws} batch:{rstats.ReferenceBatches} inst:{rstats.ReferenceInstances} " +
                    $"instDraw:{rstats.ReferenceInstancedDraws} blendDraw:{rstats.ReferenceBlendedDraws} " +
                    $"srvBinds:{rstats.ReferenceSrvBinds} meshMiss:{rstats.ReferenceMeshCacheMisses} " +
                    $"qDec:{rstats.ReferenceQueuedDecodes} actDec:{rstats.ReferenceActiveDecodes} " +
                    $"texPend:{rstats.ReferenceTexturePending} " +
                    $"cpuHit:{rstats.ReferenceCpuDecodedMeshCacheHits} bcTex:{rstats.ReferenceCompressedTextureUploads} rgbaTex:{rstats.ReferenceRgbaTextureUploads} " +
                    $"cull:{rstats.ReferenceCullMilliseconds:0.0} mesh:{rstats.ReferenceMeshUploadMilliseconds:0.0} " +
                    $"cb:{rstats.ReferenceCbUpdateMilliseconds:0.0} srv:{rstats.ReferenceSrvBindMilliseconds:0.0} " +
                    $"draw:{rstats.ReferenceDrawCallMilliseconds:0.0}ms";
            }
        }

        if (!string.Equals(_lastHudText, text, StringComparison.Ordinal))
        {
            _lastHudText = text;
            HudText.Text = text;
        }
        if (!_hudHidden && StatusOverlay.Visibility != Visibility.Visible)
        {
            HudPanel.Visibility = Visibility.Visible;
        }
    }

    private void HudToggleButton_Changed(object sender, RoutedEventArgs e)
    {
        _hudHidden = HudToggleButton.IsChecked != true;
        HudPanel.Visibility = !_hudHidden && StatusOverlay.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // Auto-dismiss timer for transient notifications (e.g. "not a teleport door"). Persistent status
    // (load progress, fatal-init errors) passes autoDismiss=false and stays until HideStatus().
    private DispatcherTimer? _statusDismissTimer;

    private void ShowStatus(string message, bool autoDismiss = false)
    {
        StatusOverlay.Text = message;
        StatusOverlay.Visibility = Visibility.Visible;
        HudPanel.Visibility = Visibility.Collapsed;

        // A new status (transient or persistent) cancels any pending auto-dismiss so a stale timer
        // can't hide a later persistent message (e.g. a door warning's timer hiding "Loading…").
        _statusDismissTimer?.Stop();
        if (autoDismiss)
        {
            _statusDismissTimer ??= CreateStatusDismissTimer();
            _statusDismissTimer.Start();
        }
    }

    private DispatcherTimer CreateStatusDismissTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            _statusDismissTimer?.Stop();
            if (StatusOverlay.Visibility == Visibility.Visible)
            {
                HideStatus();
            }
        };
        return timer;
    }

    private void HideStatus()
    {
        _statusDismissTimer?.Stop();
        StatusOverlay.Visibility = Visibility.Collapsed;
        HudPanel.Visibility = _hudHidden ? Visibility.Collapsed : Visibility.Visible;
        _lastHudUpdateTimestamp = 0;
    }
}

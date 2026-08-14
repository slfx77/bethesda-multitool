using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

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
            : "D3D12 unavailable";
        var pointerHint = ProjectionActive
            ? "drag pan   Shift+drag rotate   "
            : "drag to look   ";
        // ReferenceRenderer12 deliberately retains its last-frame diagnostics when the Meshes pass
        // is skipped. Treat a disabled parent as no current reference result rather than surfacing
        // that retained frame as if hidden meshes had just drawn.
        var belowWaterBlendedDraws = _showReferences
            ? _references?.LastBelowWaterBlendedDraws ?? 0
            : 0;
        var partitionedBlendedCandidates = _showReferences
            ? _references?.LastPartitionedBlendedCandidates ?? 0
            : 0;
        // Layer on/off state now lives in the toolbar toggle buttons, so the HUD spends the
        // freed space spelling out the movement controls.
        var text =
            $"[{backend}]   " +
            $"Cells: {visible} / {total}   refs: {visibleReferences}   nav: {visibleNavMesh}   " +
            // Placed lights previously had NO user-visible surface, so "the toggle does nothing"
            // was indistinguishable from "the lights are uploading but look subtle".
            $"lights: {_framePlacedLights.Count}   " +
            // Submerged blended submeshes reordered ahead of the water surface. Zero while water is
            // on screen means the split is inactive — the state in which underwater decals composite
            // on top of the water, which had no diagnostic surface at all before.
            $"subm: {belowWaterBlendedDraws}/{partitionedBlendedCandidates}   " +
            $"pos: ({_camera.Position.X:0}, {_camera.Position.Y:0}, {_camera.Position.Z:0})   " +
            $"yaw: {NormalizedYawDegrees():0}°   pitch: {_camera.Pitch * (180f / MathF.PI):0}°   " +
            $"speed: {_controller.MoveSpeed:0}   " +
            $"dist: {_renderDistance / _cellSize:0.#}c   " +
            $"mode: {mode}   time {hour:00}:{minute:00}\n" +
            "WASD move   Q/E up/down   mouse-wheel speed   " + pointerHint +
            "PgUp/PgDn view distance   F fly/walk   P copy camera pose   " +
            "click select (click again = cycle)   Esc deselect";

        // Draw-cap signal: when a dense frame can't fit every per-draw CB in the shared ring slot,
        // the renderer skips the overflow (instead of throwing + blanking the scene). Surface it so
        // the soft cap is visible — raise FALLOUT_VIEWER_RING_BUFFER_MB if this is persistently > 0.
        var truncatedDraws = _showReferences
            ? _references?.LastFrameDrawsTruncated ?? 0
            : 0;
        if (truncatedDraws > 0)
        {
            var ringTotalMib = _ringBuffer12 is not null ? _ringBuffer12.BytesPerFrame / (1024.0 * 1024.0) : 0;
            text += $"\n⚠ DRAW-CAP: {truncatedDraws} draws skipped this frame " +
                    $"(ring {ringTotalMib:0}MiB full — raise FALLOUT_VIEWER_RING_BUFFER_MB)";
        }
        // A dead reference pipeline means terrain still draws while every placed object silently
        // vanishes — indistinguishable from an empty cell. Keep the reason on screen rather than
        // only in the log (a transient ShowStatus would be overwritten by the worldspace load).
        if (_referencePipelineInitError is { Length: > 0 } referenceError)
        {
            text += $"\n⚠ PLACED OBJECTS UNAVAILABLE: {referenceError}";
        }
        if (_showFrameStats && _showTerrain && _terrain is not null)
        {
            var stats = _terrain.LastStats;
            text +=
                $"\nstats cand:{stats.VisibleCandidates} draw:{stats.TerrainDraws} " +
                $"up:{stats.NewUploads} texMiss:{stats.TextureCacheMisses} " +
                $"water:{visibleWater} cpu:{stats.CpuFrameMilliseconds:0.0}ms";
        }

        // Reference stats are independent of Terrain; hiding LAND must not hide a current Meshes
        // diagnostic, and hiding Meshes must not display the renderer's retained prior frame.
        if (_showFrameStats && _showReferences && _references is not null)
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

    /// <summary>Camera yaw in display degrees, wrapped to [0, 360).</summary>
    private float NormalizedYawDegrees()
    {
        var yawDegrees = _camera.Yaw * (180f / MathF.PI) % 360f;
        return yawDegrees < 0f ? yawDegrees + 360f : yawDegrees;
    }

    /// <summary>
    ///     Copies the current camera pose to the clipboard as ready-to-run
    ///     <c>BethesdaRendererProfiler --capture-frame</c> arguments, so a spot found in the live
    ///     viewer can be replayed headlessly (bug repros, A/B captures). The mapping is 1:1 with the
    ///     profiler's aimed-capture path: center-x/y/z are raw world units into
    ///     <c>Profiler_SetCameraPose</c>, yaw/pitch are degrees × π/180 into the camera's
    ///     radian fields (pitch positive looks up). FOV (--capture-fov) and the live panel size
    ///     (--capture-width/height) are emitted too so the replay matches the on-screen zoom + aspect —
    ///     omitting them silently reframed the capture at the profiler's 60° / default-size defaults,
    ///     which looked like a different camera angle. The profiler text remains the exact clipboard
    ///     prefix; a game-specific console teleport block (or an explicit unsupported explanation) is
    ///     appended. Also logged, in case the clipboard is lost.
    /// </summary>
    private void CopyCameraPoseToClipboard()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var yawDegrees = NormalizedYawDegrees();
        var pitchDegrees = _camera.Pitch * (180f / MathF.PI);
        // FOV and the panel aspect are NOT part of the camera position/orientation, but they change the
        // apparent framing — a capture that omits them (the profiler's 60° / 1.6:1 defaults) reproduces
        // the same look direction at a different zoom/width, which reads as "the angle isn't the same".
        // Emit both so a replayed pose matches what the live viewer showed.
        var fovDegrees = _camera.FovYRadians * (180f / MathF.PI);
        var panelW = (int)Math.Round(RenderPanel.ActualWidth);
        var panelH = (int)Math.Round(RenderPanel.ActualHeight);
        var sizeArg = panelW > 0 && panelH > 0
            ? string.Format(inv, " --capture-width {0} --capture-height {1}", panelW, panelH)
            : string.Empty;

        // Interior captures target the cell; exterior captures target the worldspace by EditorID.
        string target;
        if (_selectedInterior is { } interior)
        {
            var cellId = string.IsNullOrEmpty(interior.EditorId)
                ? string.Format(inv, "0x{0:X8}", interior.FormId)
                : interior.EditorId;
            target = string.Format(inv, "--capture-interior {0}", cellId);
        }
        else
        {
            target = string.Format(
                inv, "--capture-worldspace-name {0}", Profiler_SelectedWorldspaceEditorId ?? "(none)");
        }

        var weather = !_weatherSelectionIsClimateDefault && _selectedWeather?.EditorId is { Length: > 0 } w
            ? string.Format(inv, " --capture-weather {0}", w)
            : string.Empty;

        var pose = string.Format(
            inv,
            "{0} --capture-center-x {1:0.#} --capture-center-y {2:0.#} --capture-z {3:0.#} " +
            "--capture-yaw {4:0.#} --capture-pitch {5:0.#} --capture-fov {6:0.#} " +
            "--capture-hour {7:0.##}{8}{9}",
            target,
            _camera.Position.X,
            _camera.Position.Y,
            _camera.Position.Z,
            yawDegrees,
            pitchDegrees,
            fovDegrees,
            _gameHour,
            weather,
            sizeArg);

        var game = _data?.Game ?? BethesdaGame.Unknown;
        // TES3 CELL NAME is the console target and is retained as FullName. Later games require the
        // EditorID; never feed their localized display name (or our synthetic FormID fallback) to COC.
        var interiorConsoleTarget = game == BethesdaGame.Morrowind
            ? _selectedInterior?.FullName
            : _selectedInterior?.EditorId;
        var teleport = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            game,
            _camera.Position,
            yawDegrees,
            pitchDegrees,
            _selectedInterior is not null,
            interiorConsoleTarget,
            Profiler_SelectedWorldspaceEditorId,
            _cellSize));
        var clipboardText = InGameTeleportCommandFormatter.AppendToProfilerPose(pose, teleport);

        ClipboardHelper.CopyText(clipboardText);
        Log.Info("[CameraPose] {0}", pose);
        Log.Info("[CameraTeleport] {0}", teleport.Text);
        ShowStatus($"Camera pose + in-game teleport help copied:\n{clipboardText}", autoDismiss: true);
    }

    private void HudToggleButton_Changed(object sender, RoutedEventArgs e)
    {
        _hudHidden = (sender as ToggleButton)?.IsChecked != true;
        // Fires DURING XAML load: IsChecked="True" raises Checked as the markup assigns it, and the
        // toggle now sits in the toolbar, which the parser builds BEFORE the viewport panels below
        // it. The x:Name fields for those are still null at that point, and the exception thrown
        // out of a property assignment surfaces only as "Failed to assign to property
        // ToggleButton.IsChecked" — so guard rather than reorder the markup. ApplyHudVisibility on
        // the next HUD update reconciles whatever this call skipped.
        if (HudPanel is null || StatusOverlay is null) return;
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
        // Short dismiss: these are transient hints ("Selected object is not a teleport door"); the
        // former 4-second interval read as a lingering error banner (part9 feedback).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
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

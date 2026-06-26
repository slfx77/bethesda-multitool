using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Projection control for the 3D viewer: the "Projection" toolbar dropdown (None / Orthographic /
///     Isometric / Trimetric), the perspective-only FOV slider, and the ◄ ► 90°-snap buttons.
///     <para>
///         <see cref="ProjectionMode.None" /> is the live perspective camera (<see cref="_camera" /> +
///         <see cref="_controller" />, unchanged). In the orthographic modes the camera is interactive
///         but constrained: pitch is locked to the mode's elevation, the ◄ ► buttons snap the yaw to 90°
///         quadrants (Shift+drag rotates it freely; the next ◄ ► click re-snaps), left-drag pans the
///         focus point in the view plane, and the wheel zooms the ortho extent. The
///         matrices come from the shared <see cref="OrthoViewProjBuilder" /> (verified by
///         <c>OrthoViewProjBuilderTests</c>), so the live ortho view, picking, and the 3D export all
///         agree on orientation + depth.
///     </para>
/// </summary>
public sealed partial class WorldView3DControl
{
    private ProjectionMode _projectionMode = ProjectionMode.None;
    // Live camera azimuth in DEGREES (continuous). The aligned positions are OrthoViewProjBuilder's
    // quadrants — base + k·90 (base 0 for orthographic / 45 for iso-tri). The ◄ ► buttons snap to those
    // (StepAzimuth); Shift+drag rotates freely off them (RotateProjectionAzimuth), after which the next
    // ◄ ► click snaps to the nearest aligned increment in the arrow's direction. Preserved (re-aligned
    // to the new base) across ortho↔iso/tri switches.
    private float _azimuthDeg;
    // Degrees of free azimuth rotation per pixel of Shift+drag (~half a turn across a 450px drag).
    private const float AzimuthDragSensitivityDeg = 0.4f;
    // World point the ortho camera centers on (the look-at target). Seeded from the perspective
    // camera's ground intersection when a mode is first entered; moved by left-drag panning.
    private Vector3 _projectionFocus;
    // Half the vertical world extent the ortho frustum covers (the zoom). Driven by the wheel.
    private float _orthoHalfHeight = 8f * WorldGridConstants.CellSize;

    // FOV slider bounds (perspective-only). Default 60° matches CameraState.FovYRadians (π/3).
    private const float MinFovDegrees = 30f;
    private const float MaxFovDegrees = 110f;
    private const float DefaultFovDegrees = 60f;

    private bool ProjectionActive => _projectionMode != ProjectionMode.None;

    // ---- Toolbar handlers ------------------------------------------------------------------------

    private void ProjectionModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        // RadioButtons item order matches the ProjectionMode enum (None=0, Orthographic=1, …).
        var index = ProjectionModeRadios.SelectedIndex;
        if (index < 0) return;
        SetProjectionMode((ProjectionMode)index);
    }

    private void FovSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        var degrees = Math.Clamp((float)e.NewValue, MinFovDegrees, MaxFovDegrees);
        _camera.FovYRadians = degrees * (MathF.PI / 180f);
    }

    private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => StepAzimuth(-1);
    private void RotateRightButton_Click(object sender, RoutedEventArgs e) => StepAzimuth(+1);

    // ---- State transitions -----------------------------------------------------------------------

    private void SetProjectionMode(ProjectionMode mode)
    {
        if (mode == _projectionMode) return;
        var wasActive = ProjectionActive;
        var prevMode = _projectionMode;
        _projectionMode = mode;

        if (ProjectionActive)
        {
            if (!wasActive)
            {
                // Entering an ortho mode from the perspective camera: frame what the camera was looking
                // at and start at the mode's first quadrant (north for ortho, NE for iso/tri).
                InitProjectionFocusFromCamera();
                _azimuthDeg = OrthoViewProjBuilder.AzimuthDegFor(mode, 0);
            }
            else
            {
                // Switching ortho↔iso/tri: preserve the current quadrant orientation, re-aligned to the
                // new mode's base (also clears any free Shift+drag rotation so the diamond reads cleanly).
                var prevBase = OrthoViewProjBuilder.AzimuthDegFor(prevMode, 0);
                var quadrant = (int)MathF.Round((_azimuthDeg - prevBase) / 90f);
                _azimuthDeg = OrthoViewProjBuilder.AzimuthDegFor(mode, quadrant);
            }
            ProjectionSnapPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // Back to perspective: drop the free camera onto the focus so the transition isn't jarring.
            RestorePerspectiveFromProjection();
            ProjectionSnapPanel.Visibility = Visibility.Collapsed;
        }

        // FOV applies to the perspective camera only — disable + dim it in the ortho modes.
        FovSlider.IsEnabled = !ProjectionActive;
        FovLabel.Opacity = ProjectionActive ? 0.4 : 1.0;
    }

    /// <summary>
    ///     ◄ ► snap rotation. When the azimuth is already aligned to a 90° increment, steps a full 90°.
    ///     When it has been rotated off-axis by a Shift+drag, snaps to the nearest aligned increment in
    ///     the arrow's direction instead (right → the next increment above, left → the next below), so the
    ///     first click re-aligns and subsequent clicks are clean 90° steps.
    /// </summary>
    private void StepAzimuth(int direction)
    {
        if (!ProjectionActive) return;
        // Pivot around the ground at screen-centre: re-seat the focus onto the terrain first (image-
        // preserving, so no jump) so rotation orbits the point the user is actually looking at. With pan
        // now seating too, the focus is already grounded here — this is just insurance.
        SeatFocusAlongView();
        _azimuthDeg = OrthoViewProjBuilder.SnapAzimuth(_azimuthDeg, _projectionMode, direction);
    }

    /// <summary>Shift+drag free rotation: turns the camera azimuth by the horizontal drag, leaving it
    /// off the 90° increments until the next ◄ ► click re-snaps. Horizontal only — pitch stays locked.</summary>
    private void RotateProjectionAzimuth(float pixelDeltaX)
    {
        if (!ProjectionActive) return;
        // Pivot around the ground at screen-centre (see StepAzimuth) — image-preserving re-seat, so the
        // free spin doesn't jump even right after a pan.
        SeatFocusAlongView();
        _azimuthDeg = NormalizeAzimuth(_azimuthDeg + (pixelDeltaX * AzimuthDragSensitivityDeg));
    }

    private static float NormalizeAzimuth(float deg)
    {
        deg %= 360f;
        return deg < 0f ? deg + 360f : deg;
    }

    /// <summary>
    ///     Seeds the ortho focus from the perspective camera: intersects the camera's forward ray with
    ///     the world Z=0 plane (falling back to the point straight below the camera when it's looking
    ///     level), and frames the ortho extent to the current streaming radius.
    /// </summary>
    private void InitProjectionFocusFromCamera()
    {
        var pos = _camera.Position;
        var fwd = _camera.Forward;
        Vector3 focus;
        if (MathF.Abs(fwd.Z) > 1e-3f)
        {
            var t = -pos.Z / fwd.Z; // distance along the ray to Z=0
            focus = t > 0f ? pos + (fwd * t) : new Vector3(pos.X, pos.Y, 0f);
        }
        else
        {
            focus = new Vector3(pos.X, pos.Y, 0f);
        }

        _projectionFocus = focus;
        SeatFocusOnGround();
        // Frame ~ the streaming radius, but cap so a large render distance doesn't open with the whole
        // worldspace loading at once. Clamp into the wheel-zoom range.
        _orthoHalfHeight = Math.Clamp(
            MathF.Min(_renderDistance * 0.5f, 12f * _cellSize), MinOrthoHalfHeight, MaxOrthoHalfHeight);
    }

    /// <summary>
    ///     Seats the ortho focus ON the terrain at its current XY (the focus Z = the sampled ground
    ///     height). CRITICAL for the tilted (iso/tri) modes: a focus Z that misses the real ground height
    ///     by ΔZ shifts the ENTIRE visible scene down-range by ΔZ / tan(elevation) — the focus point
    ///     (always rendered at screen centre) then floats above/below the ground the user actually sees,
    ///     so (a) rotation appears to pivot off-centre and (b) on zoom-in the visible ground slides out of
    ///     the focus-centred cull cylinder, blanking the screen from a corner inward. (Top-down is immune:
    ///     tan(90°) = ∞ ⇒ zero shift — which is exactly why the bug was iso/tri-only.) Seeding the focus on
    ///     the Z=0 plane left ΔZ = the whole Mojave terrain height. Pure terrain height (no object raycast:
    ///     that needs an eye Z the ortho camera doesn't have); leaves Z unchanged when the cell under the
    ///     new XY hasn't streamed in, rather than snapping to an unknown ground.
    /// </summary>
    private void SeatFocusOnGround()
    {
        if (_cellGridLookup is null) return;
        if (TerrainHeightSampler.Sample(
                _cellGridLookup, _projectionFocus.X, _projectionFocus.Y, _data?.RenderCache, _cellSize)
            is { } groundZ)
        {
            _projectionFocus.Z = groundZ;
        }
    }

    /// <summary>
    ///     Re-seats the focus onto the terrain at screen-centre by sliding it ALONG the view ray, which
    ///     leaves the rendered image unchanged: translating an orthographic look-at (focus + eye together)
    ///     along the view direction only re-anchors depth, never the projected XY. Because it can't jump
    ///     the image, it runs on EVERY interactive gesture (pan / zoom / rotate), keeping the focus
    ///     continuously on the visible ground. That fixes the two coupled iso/tri bugs: zoom-in culling
    ///     from a corner (the focus-centred cull cylinder was off the parallax-shifted ground) and
    ///     rotate-after-pan "resnapping" (pan left the focus off the ground, so rotate's straight-up
    ///     re-seat shifted the image). Top-down is unaffected — tan(90°)=∞ ⇒ no parallax to correct.
    ///     <para>
    ///         A straight-up Z re-seat (<see cref="SeatFocusOnGround" />) is kept only for first entry,
    ///         where there is no prior image to preserve and keeping focus.XY exactly on the framed point
    ///         is preferable.
    ///     </para>
    /// </summary>
    private void SeatFocusAlongView()
    {
        if (_cellGridLookup is null || !ProjectionActive) return;
        var elevation = OrthoViewProjBuilder.ElevationDegFor(_projectionMode);
        var sinEl = MathF.Sin(elevation * (MathF.PI / 180f));
        if (sinEl < 1e-3f) return; // degenerate; elevations here are 25.66–90°
        var toEye = OrthoViewProjBuilder.EyeDirection(_azimuthDeg, elevation);

        // Two passes converge the XY: each move shifts focus.XY by ΔZ·cot(el), changing the ground under
        // the focus. The image is preserved on every pass regardless, so any residual only nudges the cull
        // centre, which the radius slack absorbs.
        for (var i = 0; i < 2; i++)
        {
            if (TerrainHeightSampler.Sample(
                    _cellGridLookup, _projectionFocus.X, _projectionFocus.Y, _data?.RenderCache, _cellSize)
                is not { } groundZ)
            {
                return; // cell not streamed in yet — leave Z; the next gesture re-seats
            }
            var dz = groundZ - _projectionFocus.Z;
            if (MathF.Abs(dz) < 1f) return;
            _projectionFocus += toEye * (dz / sinEl); // slide along the view ray onto the ground
        }
    }

    /// <summary>Places the perspective camera looking down at the ortho focus when the user switches
    /// back to None, so the view doesn't jump back to wherever the free camera was parked.</summary>
    private void RestorePerspectiveFromProjection()
    {
        _camera.Position = new Vector3(
            _projectionFocus.X, _projectionFocus.Y - _orthoHalfHeight, _projectionFocus.Z + _orthoHalfHeight);
        _camera.Yaw = 0f;
        _camera.Pitch = -MathF.PI / 4f;
    }

    // ---- Matrix + interaction helpers (shared by Frame.cs render + Input.cs picking) -------------

    private const float MinOrthoHalfHeight = 256f;
    private const float MaxOrthoHalfHeight = 64f * WorldGridConstants.CellSize;
    private const float OrthoZoomPerTick = 0.85f; // wheel up shrinks the extent (zoom in)

    /// <summary>
    ///     Builds the ortho view-projection for the active mode/quadrant/focus/zoom and the matching
    ///     cull cylinder + shading eye position. The single source the render loop and picking both call
    ///     so they stay in lockstep. Only valid while <see cref="ProjectionActive" />.
    /// </summary>
    private Matrix4x4 BuildProjectionViewProj(float aspect, out VisibilityCylinder cylinder, out Vector3 eye)
    {
        var azimuth = _azimuthDeg;
        var elevation = OrthoViewProjBuilder.ElevationDegFor(_projectionMode);
        var viewProj = OrthoViewProjBuilder.BuildViewProj(_projectionFocus, azimuth, elevation, _orthoHalfHeight, aspect);

        // Cull radius covers the visible rectangle's GROUND footprint diagonal plus a couple cells of
        // slack. The vertical screen half-extent is a view-space distance; in a tilted (iso/tri) view it
        // projects onto the ground as halfHeight / sin(elevation) — up to ~2× at 30°. Using the raw
        // view-space half-height under-sized the cull circle, so the down-screen corners fell outside it
        // and were culled ("bottom corners cut off"). Project it onto the ground for the cull extent.
        var halfW = _orthoHalfHeight * MathF.Max(aspect, 1e-4f);
        var sinEl = MathF.Sin(elevation * (MathF.PI / 180f));
        var groundHalfH = _orthoHalfHeight / MathF.Max(sinEl, 0.1f);
        var radius = MathF.Sqrt((halfW * halfW) + (groundHalfH * groundHalfH)) + (2f * _cellSize);
        cylinder = OrthoViewProjBuilder.BuildCoverCylinder(_projectionFocus, radius);
        eye = OrthoViewProjBuilder.EyePosition(_projectionFocus, azimuth, elevation);
        return viewProj;
    }

    private (Vector3 Right, Vector3 Up) ProjectionCameraBasis() => OrthoViewProjBuilder.CameraBasis(
        _azimuthDeg, OrthoViewProjBuilder.ElevationDegFor(_projectionMode));

    /// <summary>Grab-drag pan: moves the focus opposite the pixel drag so the world point under the
    /// cursor stays put. Pans in the camera's right axis (always horizontal) and the ground projection
    /// of its up axis, scaled so one screen height equals the visible world extent.</summary>
    private void PanProjectionFocus(Vector2 pixelDelta)
    {
        var height = (float)RenderPanel.ActualHeight;
        if (height <= 0f) return;
        var worldPerPixel = (2f * _orthoHalfHeight) / height;

        var (right, up) = ProjectionCameraBasis();
        // Project the up axis onto the ground so vertical drags pan across the map plane (for iso/tri
        // the camera up tilts out of plane); right is already horizontal.
        var upGround = new Vector3(up.X, up.Y, 0f);
        upGround = upGround.LengthSquared() > 1e-6f ? Vector3.Normalize(upGround) : Vector3.UnitY;

        _projectionFocus -= right * (pixelDelta.X * worldPerPixel);
        _projectionFocus += upGround * (pixelDelta.Y * worldPerPixel);
        // Re-seat the focus onto the terrain ALONG the view ray (image-preserving), so the focus stays on
        // the visible ground as you pan over slopes. Crucially this does NOT smear the pan: the seat moves
        // the focus along the view direction, which leaves the orthographic image fixed — the image moves
        // only by the horizontal drag above. Keeping the focus grounded every drag is what lets a later
        // rotate pivot in place instead of snapping (the old straight-up re-seat couldn't run here).
        SeatFocusAlongView();
    }

    /// <summary>Wheel zoom for the ortho modes: scales the extent logarithmically (wheel up = zoom in),
    /// clamped to the zoom range. Returns false when not in a projection mode (caller falls back to the
    /// flythrough controller's scroll-speed handling).</summary>
    private bool TryZoomProjection(float wheelDelta)
    {
        if (!ProjectionActive) return false;
        // Re-seat the focus on the terrain (along the view ray, image-preserving) before zooming: the cull
        // cylinder is centred on focus.XY, so a focus off the visible ground parallax-shifts the terrain
        // out of the shrinking cull circle on zoom-in (the corner-culling bug). With pan seating too the
        // focus is usually already grounded; this guarantees it.
        SeatFocusAlongView();
        var ticks = wheelDelta / 120f; // one notch = 120 raw
        _orthoHalfHeight = Math.Clamp(
            _orthoHalfHeight * MathF.Pow(OrthoZoomPerTick, ticks), MinOrthoHalfHeight, MaxOrthoHalfHeight);
        return true;
    }
}
